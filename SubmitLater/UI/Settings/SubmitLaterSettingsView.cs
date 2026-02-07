using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.ViewControllers;
using SubmitLater.Gameplay;
using UnityEngine;

namespace SubmitLater.UI.Settings
{
    /// <summary>
    /// Standalone ViewController for PlayFirst SubmitLater settings.
    /// </summary>
    [ViewDefinition("SubmitLater.UI.Views.PlayFirstSubmitLaterGameplaySetup.bsml")]
    public class SubmitLaterViewController : BSMLAutomaticViewController
    {
        private static SubmitLaterViewController _instance;

        public static SubmitLaterViewController Instance
        {
            get
            {
                if (_instance == null)
                    _instance = BeatSaberUI.CreateViewController<SubmitLaterViewController>();
                return _instance;
            }
        }

        [UIValue("playFirstSubmitLaterEnabled")]
        public bool PlayFirstSubmitLaterEnabled
        {
            get => Plugin.Settings?.PlayFirstSubmitLaterEnabled ?? true;
            set
            {
                if (Plugin.Settings != null)
                {
                    Plugin.Settings.PlayFirstSubmitLaterEnabled = value;
                    Plugin.Log.Info($"PlayFirstSubmitLater: Feature {(value ? "ENABLED" : "DISABLED")}");
                }
                NotifyPropertyChanged(nameof(PlayFirstSubmitLaterEnabled));
                NotifyPropertyChanged(nameof(SubmissionStatus));
            }
        }

        [UIValue("scoreSubmissionEnabled")]
        public bool ScoreSubmissionEnabled
        {
            get => Plugin.Settings?.ScoreSubmissionEnabled ?? true;
            set
            {
                if (Plugin.Settings != null)
                {
                    Plugin.Settings.ScoreSubmissionEnabled = value;

                    // Apply to manager
                    if (value)
                        PlayFirstSubmitLaterManager.EnableSubmission();
                    else
                        PlayFirstSubmitLaterManager.DisableSubmission();
                }
                NotifyPropertyChanged(nameof(ScoreSubmissionEnabled));
                NotifyPropertyChanged(nameof(SubmissionStatus));
            }
        }

        [UIValue("autoPauseOnMapEnd")]
        public bool AutoPauseOnMapEnd
        {
            get => Plugin.Settings?.AutoPauseOnMapEnd ?? true;
            set
            {
                if (Plugin.Settings != null)
                    Plugin.Settings.AutoPauseOnMapEnd = value;
                NotifyPropertyChanged(nameof(AutoPauseOnMapEnd));
            }
        }



        [UIValue("submissionStatus")]
        public string SubmissionStatus
        {
            get
            {
                if (!PlayFirstSubmitLaterManager.IsFeatureEnabled)
                    return "<color=gray>Feature disabled</color>";

                if (ScoreSubmissionEnabled)
                    return "<color=green>Scores WILL be submitted to leaderboards</color>";
                else
                    return "<color=orange>Scores WILL NOT be submitted</color>";
            }
        }
    }
}
