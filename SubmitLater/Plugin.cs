using BeatSaberMarkupLanguage.GameplaySetup;
using BS_Utils.Utilities;
using HarmonyLib;
using IPA;
using IPA.Config.Stores;
using IPA.Logging;
using System.Reflection;

namespace SubmitLater
{
    [Plugin(RuntimeOptions.SingleStartInit)]
    public class Plugin
    {
        internal static Plugin Instance { get; private set; }
        internal static Logger Log { get; private set; }
        internal static PluginConfig Settings { get; private set; }

        private bool _tabRegisteredThisMenu = false;

        [Init]
        public void Init(Logger logger, IPA.Config.Config config)
        {
            Log = logger;
            Instance = this;
            Settings = config.Generated<PluginConfig>();
            PluginConfig.Instance = Settings;
            Log.Info("SubmitLater: Init");
        }

        [OnStart]
        public void OnApplicationStart()
        {
            Log.Info("SubmitLater: OnApplicationStart");

            // Initialize scene helper
            SceneHelper.Init();

            // Register menu scene event
            BSEvents.menuSceneActive += OnMenuSceneActive;

            // Initialize the Submit Later manager
            _ = Gameplay.PlayFirstSubmitLaterManager.Instance;

            // Apply Harmony patches
            try
            {
                var harmony = new Harmony("com.submitlater.beatsaber");
                harmony.PatchAll(Assembly.GetExecutingAssembly());
                Log.Info("SubmitLater: Harmony patches applied");
            }
            catch (System.Exception ex)
            {
                Log.Error($"SubmitLater: Harmony patch error: {ex}");
            }
        }

        private void OnMenuSceneActive()
        {
            Log.Info("SubmitLater: menuSceneActive");
            _tabRegisteredThisMenu = false;

            // Register gameplay setup tab using CoroutineHost
            Gameplay.CoroutineHost.Instance.StartCoroutine(RegisterGameplaySetupTabWhenReady());
        }

        private System.Collections.IEnumerator RegisterGameplaySetupTabWhenReady()
        {
            while (!_tabRegisteredThisMenu)
            {
                yield return null;

                try
                {
                    // Ensure manager is initialized
                    _ = Gameplay.PlayFirstSubmitLaterManager.Instance;

                    // Try to add the gameplay setup tab
                    var gs = BeatSaberMarkupLanguage.GameplaySetup.GameplaySetup.Instance;
                    if (gs == null) continue;

                    // FIXED: Use proper class name and property (not method call)
                    gs.AddTab(
                        "Submit Later",
                        "SubmitLater.UI.Views.PlayFirstSubmitLaterGameplaySetup.bsml",
                        SubmitLater.UI.Settings.SubmitLaterViewController.Instance

                    );

                    _tabRegisteredThisMenu = true;
                    Log.Info("SubmitLater: GameplaySetup tab registered");
                }
                catch (System.InvalidOperationException ex)
                {
                    Log.Debug($"SubmitLater: GameplaySetup not ready yet: {ex.Message}");
                }
                catch (System.Exception ex)
                {
                    Log.Error($"SubmitLater: Failed registering GameplaySetup tab: {ex}");
                    yield break;
                }
            }
        }

        [OnExit]
        public void OnApplicationQuit()
        {
            Log.Info("SubmitLater: OnApplicationQuit");

            try
            {
                SceneHelper.Dispose();
                BSEvents.menuSceneActive -= OnMenuSceneActive;
                Log.Info("SubmitLater: Clean shutdown");
            }
            catch (System.Exception ex)
            {
                Log.Error($"SubmitLater: Error during shutdown: {ex}");
            }
        }
    }
}