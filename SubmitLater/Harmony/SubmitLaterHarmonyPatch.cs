using HarmonyLib;
using System;
using System.Linq;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

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

                    // Find pause menu components
                    var pauseMenuManager = Resources.FindObjectsOfTypeAll<PauseMenuManager>()
                        .FirstOrDefault();
                    var pauseController = Resources.FindObjectsOfTypeAll<PauseController>()
                        .FirstOrDefault();

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

                        // Pause the game and show menu
                        pauseController.Pause();
                        _pauseMenuManager.ShowMenu();

                        Plugin.Log.Info("[SubmitLater] Pause menu shown - waiting for user input");

                        try
                        {
                            // Wait one frame for menu to fully initialize
                            _ = Gameplay.CoroutineHost.Instance.StartCoroutine(EnsureMenuInteractableCoroutine(_pauseMenuManager));
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log.Warn($"[SubmitLater] Could not ensure menu interactable: {ex.Message}");
                        }


                        // Focus the Continue button for better UX
                        try
                        {
                            var allButtons = _pauseMenuManager.GetComponentsInChildren<Button>(true);
                            var continueButton = allButtons.FirstOrDefault(b => b.name == "ContinueButton");

                            if (continueButton != null && EventSystem.current != null)
                            {
                                EventSystem.current.SetSelectedGameObject(continueButton.gameObject);
                                Plugin.Log.Debug("[SubmitLater] Continue button focused");
                            }
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log.Warn($"[SubmitLater] Could not focus Continue button: {ex.Message}");
                        }

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
        private static IEnumerator EnsureMenuInteractableCoroutine(PauseMenuManager pauseMenuManager)
        {
            // Wait one frame for UI to initialize
            yield return null;

            try
            {
                // Find the pause menu's Canvas and ensure raycasting is enabled
                var canvas = pauseMenuManager.GetComponentInParent<Canvas>();
                if (canvas != null)
                {
                    // Ensure GraphicRaycaster exists and is enabled
                    var raycaster = canvas.GetComponent<GraphicRaycaster>();
                    if (raycaster == null)
                    {
                        raycaster = canvas.gameObject.AddComponent<GraphicRaycaster>();
                        Plugin.Log.Debug("[SubmitLater] Added GraphicRaycaster to pause menu canvas");
                    }
                    raycaster.enabled = true;

                    // Ensure canvas is set to highest sort order to be on top
                    canvas.sortingOrder = 100;
                    Plugin.Log.Debug($"[SubmitLater] Canvas sort order set to {canvas.sortingOrder}");
                }

                // Ensure CanvasGroup allows interaction
                var canvasGroup = pauseMenuManager.GetComponent<CanvasGroup>();
                if (canvasGroup == null)
                {
                    canvasGroup = pauseMenuManager.GetComponentInParent<CanvasGroup>();
                }

                if (canvasGroup != null)
                {
                    canvasGroup.interactable = true;
                    canvasGroup.blocksRaycasts = true;
                    Plugin.Log.Debug("[SubmitLater] CanvasGroup set to interactable");
                }

                // Find and focus the Continue button
                var allButtons = pauseMenuManager.GetComponentsInChildren<Button>(true);
                var continueButton = allButtons.FirstOrDefault(b => b.name == "ContinueButton");

                if (continueButton != null)
                {
                    // Ensure button is enabled
                    continueButton.interactable = true;

                    // Clear and set selection
                    if (EventSystem.current != null)
                    {
                        EventSystem.current.SetSelectedGameObject(null);
                        
                        EventSystem.current.SetSelectedGameObject(continueButton.gameObject);
                        Plugin.Log.Debug("[SubmitLater] Continue button focused and interactable");
                    }
                }

                // Extra: Force raycast target on all buttons
                foreach (var btn in allButtons)
                {
                    if (btn != null && btn.targetGraphic != null)
                    {
                        btn.targetGraphic.raycastTarget = true;
                    }
                }

                Plugin.Log.Info("[SubmitLater] Pause menu interaction setup complete");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[SubmitLater] Error in EnsureMenuInteractableCoroutine: {ex}");
            }
        }

    }
}