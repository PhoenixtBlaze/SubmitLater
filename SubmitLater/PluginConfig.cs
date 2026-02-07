using IPA.Config.Stores;
using IPA.Config.Stores.Attributes;
using IPA.Config.Stores.Converters;

namespace SubmitLater
{
    public class PluginConfig
    {
        public static PluginConfig Instance { get; set; }

        // Submit Later Settings
        // Add these missing properties:
        public virtual bool PlayFirstSubmitLaterEnabled { get; set; } = true;
        public virtual bool ScoreSubmissionEnabled { get; set; } = true;
        public virtual bool AutoPauseOnMapEnd { get; set; } = true;
        public virtual bool SubmitOnLevelComplete { get; set; } = true;
        public virtual bool SubmitOnLevelFail { get; set; } = false;
        public virtual bool DebugMode { get; set; } = false;

        /// <summary>
        /// Called whenever the config is changed
        /// </summary>
        public virtual void Changed()
        {
            // Do stuff after config is changed
        }
        
    }
}