using UnityEngine;

namespace SubmitLater.Gameplay
{
    public class CoroutineHost : MonoBehaviour
    {
        private static CoroutineHost _instance;

        public static CoroutineHost Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("SubmitLater_CoroutineHost");
                    Object.DontDestroyOnLoad(go);
                    _instance = go.AddComponent<CoroutineHost>();
                }
                return _instance;
            }
        }
        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }
    }
}
