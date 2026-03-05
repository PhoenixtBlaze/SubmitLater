using HarmonyLib;
using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace SubmitLater.HarmonyPatches
{
    /// <summary>
    /// Intercepts level completion to pause the game and delay score submission.
    /// Blocks StandardLevelScenesTransitionSetupDataSO.Finish() until user continues from pause menu.
    /// </summary>
    [HarmonyPatch(typeof(StandardLevelScenesTransitionSetupDataSO), "Finish")]
    internal static class PlayFirstSubmitLaterPatch
    {
        private static bool _pauseGateActive = false;
        private static bool _bypassPauseGateCall = false;

        // Delegates for pause menu events
        private static Action _continueDelegate;
        private static Action _resumeFinishedDelegate;
        private static Action _menuDelegate;
        private static Action _restartDelegate;

        // Cached references for deferred execution
        private static StandardLevelScenesTransitionSetupDataSO _pendingSetup;
        private static LevelCompletionResults _pendingResults;
        private static PauseMenuManager _pauseMenuManager;

        // PauseController private members (reflected for robust end-of-level pause behavior)
        private static readonly FieldInfo PauseControllerPauseMenuManagerField =
            AccessTools.Field(typeof(PauseController), "_pauseMenuManager");
        private static readonly FieldInfo PauseControllerGamePauseField =
            AccessTools.Field(typeof(PauseController), "_gamePause");
        private static readonly FieldInfo PauseControllerBeatmapObjectManagerField =
            AccessTools.Field(typeof(PauseController), "_beatmapObjectManager");
        private static readonly FieldInfo PauseControllerPausedField =
            AccessTools.Field(typeof(PauseController), "_paused");
        private static readonly FieldInfo PauseControllerPauseChangedStateTimeField =
            AccessTools.Field(typeof(PauseController), "_pauseChangedStateTime");
        private static readonly FieldInfo PauseControllerDidPauseEventField =
            AccessTools.Field(typeof(PauseController), "didPauseEvent");

        private static PauseController FindPauseController()
        {
            var allControllers = Resources.FindObjectsOfTypeAll<PauseController>();
            return allControllers.FirstOrDefault(pc =>
                pc != null &&
                pc.gameObject != null &&
                pc.gameObject.scene.IsValid() &&
                pc.gameObject.scene.isLoaded &&
                pc.gameObject.activeInHierarchy) ?? allControllers.FirstOrDefault();
        }

        private static PauseMenuManager ResolvePauseMenuManager(PauseController pauseController)
        {
            if (pauseController != null && PauseControllerPauseMenuManagerField != null)
            {
                var fromPauseController = PauseControllerPauseMenuManagerField.GetValue(pauseController) as PauseMenuManager;
                if (fromPauseController != null)
                    return fromPauseController;
            }

            var allPauseMenus = Resources.FindObjectsOfTypeAll<PauseMenuManager>();
            return allPauseMenus.FirstOrDefault(pm =>
                pm != null &&
                pm.gameObject != null &&
                pm.gameObject.scene.IsValid() &&
                pm.gameObject.scene.isLoaded &&
                pm.gameObject.activeInHierarchy) ?? allPauseMenus.FirstOrDefault();
        }

        private static void ForcePauseControllerState(PauseController pauseController)
        {
            if (pauseController == null)
                return;

            try
            {
                if (PauseControllerPauseChangedStateTimeField != null &&
                    PauseControllerPauseChangedStateTimeField.FieldType == typeof(float))
                {
                    PauseControllerPauseChangedStateTimeField.SetValue(pauseController, Time.realtimeSinceStartup);
                }

                if (PauseControllerPausedField == null)
                    return;

                if (PauseControllerPausedField.FieldType == typeof(bool))
                {
                    PauseControllerPausedField.SetValue(pauseController, true);
                    return;
                }

                if (PauseControllerPausedField.FieldType.IsEnum)
                {
                    var pausedState = Enum.Parse(PauseControllerPausedField.FieldType, "Paused");
                    PauseControllerPausedField.SetValue(pauseController, pausedState);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[SubmitLater] Failed to force PauseController state: {ex.Message}");
            }
        }

        private static bool TryShowPauseMenuWithProperPause(PauseController pauseController, PauseMenuManager pauseMenuManager)
        {
            if (pauseController == null || pauseMenuManager == null)
                return false;

            var gamePause = PauseControllerGamePauseField?.GetValue(pauseController) as IGamePause;
            var beatmapObjectManager = PauseControllerBeatmapObjectManagerField?.GetValue(pauseController) as BeatmapObjectManager;

            // Normal pause path (preferred)
            pauseController.Pause();
            if (gamePause != null && gamePause.isPaused)
                return true;
            if (gamePause == null && pauseMenuManager.isActiveAndEnabled)
                return true;
            if (gamePause == null)
            {
                Plugin.Log.Warn("[SubmitLater] Could not access IGamePause from PauseController");
                return false;
            }

            // End-of-level path: PauseController can reject Pause() when gameplay state is Finished.
            Plugin.Log.Info("[SubmitLater] PauseController refused Pause() at level end, forcing full pause state");

            ForcePauseControllerState(pauseController);
            gamePause.Pause();
            pauseMenuManager.ShowMenu();

            if (beatmapObjectManager != null)
            {
                beatmapObjectManager.HideAllBeatmapObjects(hide: true);
                beatmapObjectManager.PauseAllBeatmapObjects(pause: true);
            }

            if (PauseControllerDidPauseEventField?.GetValue(pauseController) is Action didPauseEvent)
            {
                didPauseEvent.Invoke();
            }

            return gamePause.isPaused && pauseMenuManager.isActiveAndEnabled;
        }

        /// <summary>
        /// Determines if the pause gate should activate based on settings and level state.
        /// </summary>
        private static bool ShouldPauseGate(LevelCompletionResults results)
        {
            if (results == null)
                return false;

            // Don't interfere with multiplayer (vanilla OR MP+)
            if (BS_Utils.Plugin.LevelData.Mode == BS_Utils.Gameplay.Mode.Multiplayer
                || SubmitLater.Gameplay.PlayFirstSubmitLaterManager.IsBSPlusMultiplayerActive())
            {
                Plugin.Log.Debug("[SubmitLater] Multiplayer detected (native or MP+), skipping pause gate");
                return false;
            }


            var settings = Plugin.Settings;
            if (settings == null || !settings.PlayFirstSubmitLaterEnabled)
            {
                Plugin.Log.Debug("[SubmitLater] Feature disabled in settings");
                return false;
            }

            // Don't pause if user manually quit or restarted
            if (results.levelEndAction == LevelCompletionResults.LevelEndAction.Quit ||
                results.levelEndAction == LevelCompletionResults.LevelEndAction.Restart)
            {
                Plugin.Log.Debug("[SubmitLater] User manually quit/restarted, skipping pause");
                return false;
            }

            // Check if we should pause on failure
            if (results.levelEndStateType == LevelCompletionResults.LevelEndStateType.Failed)
            {
                if (!settings.SubmitOnLevelFail)
                {
                    Plugin.Log.Debug("[SubmitLater] Level failed but submit-on-fail disabled");
                    return false;
                }
            }

            // Check if we should pause on completion
            if (results.levelEndStateType == LevelCompletionResults.LevelEndStateType.Cleared)
            {
                if (!settings.SubmitOnLevelComplete)
                {
                    Plugin.Log.Debug("[SubmitLater] Level completed but submit-on-complete disabled");
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Unsubscribes from all pause menu events and clears references.
        /// </summary>
        private static void CleanupPauseListeners()
        {
            if (_pauseMenuManager != null)
            {
                if (_continueDelegate != null)
                    _pauseMenuManager.didPressContinueButtonEvent -= _continueDelegate;
                if (_resumeFinishedDelegate != null)
                    _pauseMenuManager.didFinishResumeAnimationEvent -= _resumeFinishedDelegate;
                if (_menuDelegate != null)
                    _pauseMenuManager.didPressMenuButtonEvent -= _menuDelegate;
                if (_restartDelegate != null)
                    _pauseMenuManager.didPressRestartButtonEvent -= _restartDelegate;
            }

            _continueDelegate = null;
            _resumeFinishedDelegate = null;
            _menuDelegate = null;
            _restartDelegate = null;
            _pauseMenuManager = null;
        }

        /// <summary>
        /// Clears the pause gate state completely.
        /// </summary>
        private static void ClearPauseGate()
        {
            CleanupPauseListeners();
            _pauseGateActive = false;
            _pendingSetup = null;
            _pendingResults = null;
            Plugin.Log.Debug("[SubmitLater] Pause gate cleared");
        }

        /// <summary>
        /// Called when user presses Continue button.
        /// </summary>
        private static void HandleContinuePressed()
        {
            Plugin.Log.Info("[SubmitLater] Continue button pressed");
        }

        /// <summary>
        /// Called after resume animation finishes. 
        /// Now we actually call the original Finish() method.
        /// </summary>
        private static void HandleResumeFinished()
        {
            Plugin.Log.Info("[SubmitLater] Resume animation finished");

            if (_pendingSetup == null)
            {
                Plugin.Log.Warn("[SubmitLater] No pending setup, clearing gate");
                ClearPauseGate();
                return;
            }

            try
            {
                // Set bypass flag so our Prefix doesn't intercept again
                _bypassPauseGateCall = true;

                Plugin.Log.Info("[SubmitLater] Calling original Finish() - score will now submit");
                _pendingSetup.Finish(_pendingResults);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[SubmitLater] Error calling Finish: {ex}");
            }
            finally
            {
                _bypassPauseGateCall = false;
                ClearPauseGate();
            }
        }

        /// <summary>
        /// Called when user presses Menu or Restart buttons  (abort submission).
        /// </summary>
        private static void HandleAbort()
        {
            Plugin.Log.Info("[SubmitLater] User aborted (menu/restart), clearing gate");
            ClearPauseGate();
        }

        /// <summary>
        /// Harmony Prefix - intercepts Finish() to implement pause gate logic.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        private static bool Prefix(
            StandardLevelScenesTransitionSetupDataSO __instance,
            LevelCompletionResults levelCompletionResults)
        {
            try
            {
                // Sanity checks
                if (__instance == null || levelCompletionResults == null)
                    return true;

                // If this is our own call from HandleResumeFinished, allow it through
                if (_bypassPauseGateCall)
                {
                    Plugin.Log.Debug("[SubmitLater] Bypass flag set, allowing Finish() to proceed");
                    return true;
                }

                // Check if we should activate the pause gate
                if (!_pauseGateActive && ShouldPauseGate(levelCompletionResults))
                {
                    Plugin.Log.Info("[SubmitLater] Activating pause gate - blocking score submission");

                    _pauseGateActive = true;
                    _pendingSetup = __instance;
                    _pendingResults = levelCompletionResults;

                    // Clean up any existing listeners
                    CleanupPauseListeners();

                    // Find pause components from active gameplay scene
                    var pauseController = FindPauseController();
                    var pauseMenuManager = ResolvePauseMenuManager(pauseController);

                    if (pauseMenuManager != null && pauseController != null)
                    {
                        _pauseMenuManager = pauseMenuManager;

                        // Create and subscribe event delegates
                        _continueDelegate = HandleContinuePressed;
                        _resumeFinishedDelegate = HandleResumeFinished;
                        _menuDelegate = HandleAbort;
                        _restartDelegate = HandleAbort;

                        _pauseMenuManager.didPressContinueButtonEvent += _continueDelegate;
                        _pauseMenuManager.didFinishResumeAnimationEvent += _resumeFinishedDelegate;
                        _pauseMenuManager.didPressMenuButtonEvent += _menuDelegate;
                        _pauseMenuManager.didPressRestartButtonEvent += _restartDelegate;

                        if (!TryShowPauseMenuWithProperPause(pauseController, _pauseMenuManager))
                        {
                            Plugin.Log.Warn("[SubmitLater] Failed to enter a stable pause state, allowing Finish()");
                            ClearPauseGate();
                            return true;
                        }

                        Plugin.Log.Info("[SubmitLater] Pause menu shown - waiting for user input");

                        // Block the original Finish() call
                        return false;
                    }
                    else
                    {
                        Plugin.Log.Warn("[SubmitLater] Could not find pause menu components, allowing Finish()");
                        ClearPauseGate();
                        return true;
                    }
                }

                // Allow original method to execute
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[SubmitLater] Error in Harmony Prefix: {ex}");
                ClearPauseGate();
                return true; // Always allow through on error
            }
        }

    }
}
