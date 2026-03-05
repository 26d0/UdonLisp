using TMPro;
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

namespace UdonLisp
{
    /// <summary>
    /// Manages networked REPL state for UdonLisp.
    /// Lives on its own GameObject (separate from LispRunner) to avoid sync-mode conflicts.
    /// 
    /// Design: syncs the history of evaluated expressions as a string.
    /// Each client re-evaluates the history to reconstruct the same environment.
    /// Only the owner can type and run code; others observe.
    /// Ownership can be transferred via the "Take Control" button.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class LispNetworkManager : UdonSharpBehaviour
    {
        // ---- Inspector references ----

        [Header("Core References")]
        [SerializeField] private LispRunner lispRunner;
        [SerializeField] private TMP_InputField inputField;
        [SerializeField] private TextMeshProUGUI outputText;

        [Header("Networking UI")]
        [SerializeField] private TextMeshProUGUI statusLabel;
        [SerializeField] private Button runButton;
        [SerializeField] private Button takeControlButton;

        // ---- Synced state ----

        /// <summary>
        /// All evaluated expressions, separated by EXPR_DELIMITER.
        /// Late joiners replay this to reconstruct the environment.
        /// </summary>
        [UdonSynced] private string syncedHistory = "";

        /// <summary>Mirror of the input field text for display on non-owners.</summary>
        [UdonSynced] private string syncedInput = "";

        /// <summary>Mirror of the output label text for display on non-owners.</summary>
        [UdonSynced] private string syncedOutput = "";

        /// <summary>Monotonic counter incremented on each eval. Used for delta replay.</summary>
        [UdonSynced] private int historyVersion = 0;

        /// <summary>playerId of the player who currently controls the REPL.</summary>
        [UdonSynced] private int ownerPlayerId = -1;

        // ---- Local state ----

        /// <summary>Last applied history version on this client.</summary>
        private int localVersion = 0;

        /// <summary>
        /// Set to true after a full history replay that contained render-painter
        /// calls but images weren't loaded yet. Checked in OnImageReady().
        /// </summary>
        [System.NonSerialized] public bool pendingRenderReplay = false;

        // ---- Constants ----

        private const string EXPR_DELIMITER = "\n;;--EXPR--\n";
        private const int MAX_HISTORY_LENGTH = 100000;

        // ================================================================
        //  Lifecycle
        // ================================================================

        private void Start()
        {
            // The first player to join becomes the initial owner (VRChat default).
            // Set up the UI to reflect this.
            _UpdateOwnershipUI();
        }

        // ================================================================
        //  Public entry points (called via SendCustomEvent from UI)
        // ================================================================

        /// <summary>
        /// Called when the Run button is pressed.
        /// Only the owner can evaluate code; non-owners are silently ignored.
        /// </summary>
        public void OnRunButtonClicked()
        {
            // Guard: only the owner can run code
            if (!Networking.IsOwner(Networking.LocalPlayer, gameObject))
            {
                Debug.LogWarning("[LispNet] Not owner — ignoring Run.");
                return;
            }

            if (inputField == null || outputText == null || lispRunner == null) return;

            string source = inputField.text;
            if (string.IsNullOrEmpty(source))
            {
                outputText.text = "";
                return;
            }

            Debug.Log("[LispNet] Owner eval: " + source);

            // Evaluate locally on the owner
            string result = lispRunner.EvalSource(source);

            // Update local UI
            outputText.text = result;

            // Append to synced history
            if (string.IsNullOrEmpty(syncedHistory))
                syncedHistory = source;
            else
                syncedHistory = syncedHistory + EXPR_DELIMITER + source;

            syncedInput = source;
            syncedOutput = result;
            historyVersion++;

            _TruncateHistoryIfNeeded();

            Debug.Log("[LispNet] Syncing version " + historyVersion
                + " (history " + syncedHistory.Length + " chars)");
            RequestSerialization();
        }

        /// <summary>
        /// Called when a player clicks the "Take Control" button.
        /// Requests ownership of this networked object.
        /// </summary>
        public void RequestControl()
        {
            VRCPlayerApi local = Networking.LocalPlayer;
            if (local == null) return;

            if (Networking.IsOwner(local, gameObject))
            {
                Debug.Log("[LispNet] Already owner.");
                return;
            }

            Debug.Log("[LispNet] Requesting control...");
            Networking.SetOwner(local, gameObject);
        }

        /// <summary>
        /// Called from LispRunner.OnImageLoadSuccess (indirectly) when images
        /// finish downloading. If we had a pending render, re-evaluate the
        /// history to refresh the canvas. Uses ReplayDelta from 0 instead of
        /// ReplayHistory to avoid resetting globalEnv (which would wipe the
        /// rogers definition that OnImageLoadSuccess just added).
        /// </summary>
        public void OnImageReady()
        {
            if (!pendingRenderReplay) return;
            if (localVersion <= 0) return;

            Debug.Log("[LispNet] Image loaded — re-evaluating history for canvas refresh.");
            pendingRenderReplay = false;
            lispRunner.ReplayDelta(syncedHistory, 0);
            // localVersion stays the same — env is identical, just re-rendered
        }

        // ================================================================
        //  VRChat Networking Callbacks
        // ================================================================

        /// <summary>
        /// Called on non-owners when synced variables are updated.
        /// Also called for late joiners receiving initial state.
        /// </summary>
        public override void OnDeserialization()
        {
            // Update UI from synced strings
            if (inputField != null) inputField.text = syncedInput;
            if (outputText != null) outputText.text = syncedOutput;

            // Delta replay: only eval new expressions since our last version
            if (historyVersion > localVersion)
            {
                if (localVersion == 0 && !string.IsNullOrEmpty(syncedHistory))
                {
                    // Late joiner: full replay from scratch
                    Debug.Log("[LispNet] Late join — full replay (version " + historyVersion + ")");
                    string lastResult = lispRunner.ReplayHistory(syncedHistory);
                    // Check if history contains render-painter but images aren't loaded
                    if (syncedHistory.Contains("render-painter") && !_ImagesLoaded())
                    {
                        pendingRenderReplay = true;
                        Debug.Log("[LispNet] Render commands found but images not loaded — will re-replay when ready.");
                    }
                }
                else if (!string.IsNullOrEmpty(syncedHistory))
                {
                    // Incremental: replay only the delta
                    Debug.Log("[LispNet] Delta replay: " + localVersion + " -> " + historyVersion);
                    string lastResult = lispRunner.ReplayDelta(syncedHistory, localVersion);
                    // Check if the new expressions contain render-painter but images aren't loaded
                    if (syncedHistory.Contains("render-painter") && !_ImagesLoaded())
                    {
                        pendingRenderReplay = true;
                        Debug.Log("[LispNet] Render commands in delta but images not loaded — will re-replay when ready.");
                    }
                }

                localVersion = historyVersion;
            }

            _UpdateOwnershipUI();
        }

        /// <summary>Always approve ownership transfers.</summary>
        public override bool OnOwnershipRequest(VRCPlayerApi requestingPlayer, VRCPlayerApi requestedOwner)
        {
            return true;
        }

        /// <summary>
        /// Called on all clients when ownership changes.
        /// The new owner updates the ownerPlayerId and syncs it.
        /// </summary>
        public override void OnOwnershipTransferred(VRCPlayerApi newOwner)
        {
            Debug.Log("[LispNet] Ownership transferred to: "
                + (newOwner != null ? newOwner.displayName : "unknown"));

            // The new owner sets their player ID and syncs
            if (newOwner != null && newOwner.isLocal)
            {
                ownerPlayerId = newOwner.playerId;
                RequestSerialization();
            }

            _UpdateOwnershipUI();
        }

        /// <summary>Update UI when a player joins.</summary>
        public override void OnPlayerJoined(VRCPlayerApi player)
        {
            // Refresh the status display (owner name may need resolving)
            _UpdateOwnershipUI();
        }

        /// <summary>Update UI when a player leaves.</summary>
        public override void OnPlayerLeft(VRCPlayerApi player)
        {
            // If the leaving player was the owner, VRChat auto-reassigns.
            // OnOwnershipTransferred will fire. Just refresh UI here.
            _UpdateOwnershipUI();
        }

        // ================================================================
        //  Internal Helpers
        // ================================================================

        /// <summary>
        /// Update all ownership-related UI: status label, input/button interactability.
        /// </summary>
        private void _UpdateOwnershipUI()
        {
            VRCPlayerApi local = Networking.LocalPlayer;
            if (local == null) return;

            bool isOwner = Networking.IsOwner(local, gameObject);

            // Status label: show who has control
            if (statusLabel != null)
            {
                string ownerName = _GetOwnerDisplayName();
                statusLabel.text = "Controller: " + ownerName;
            }

            // Input field: only the owner can type
            if (inputField != null)
                inputField.interactable = isOwner;

            // Run button: only the owner can run
            if (runButton != null)
                runButton.interactable = isOwner;

            // Take Control button: always available (anyone can request)
            // Optionally disable if already owner
            if (takeControlButton != null)
                takeControlButton.interactable = !isOwner;
        }

        /// <summary>
        /// Get the display name of the current owner.
        /// </summary>
        private string _GetOwnerDisplayName()
        {
            // Try to resolve from ownerPlayerId
            if (ownerPlayerId > 0)
            {
                VRCPlayerApi ownerPlayer = VRCPlayerApi.GetPlayerById(ownerPlayerId);
                if (ownerPlayer != null && Utilities.IsValid(ownerPlayer))
                    return ownerPlayer.displayName;
            }

            // Fallback: use Networking.GetOwner
            VRCPlayerApi netOwner = Networking.GetOwner(gameObject);
            if (netOwner != null && Utilities.IsValid(netOwner))
                return netOwner.displayName;

            return "(unknown)";
        }

        /// <summary>
        /// Truncate the oldest expressions from syncedHistory when it exceeds
        /// MAX_HISTORY_LENGTH characters. Preserves the most recent expressions.
        /// </summary>
        private void _TruncateHistoryIfNeeded()
        {
            if (syncedHistory.Length <= MAX_HISTORY_LENGTH) return;

            Debug.LogWarning("[LispNet] History exceeds " + MAX_HISTORY_LENGTH
                + " chars (" + syncedHistory.Length + ") — truncating oldest entries.");

            // Find delimiter positions and remove from the front until under limit
            while (syncedHistory.Length > MAX_HISTORY_LENGTH)
            {
                int delimIdx = syncedHistory.IndexOf(EXPR_DELIMITER);
                if (delimIdx < 0)
                {
                    // Single huge expression — can't truncate further
                    break;
                }
                // Remove the first expression (everything up to and including the delimiter)
                syncedHistory = syncedHistory.Substring(delimIdx + EXPR_DELIMITER.Length);
            }

            Debug.Log("[LispNet] History truncated to " + syncedHistory.Length + " chars.");
        }

        /// <summary>
        /// Check if source images have been downloaded (at least index 0).
        /// Used to decide whether render-painter calls will succeed.
        /// </summary>
        private bool _ImagesLoaded()
        {
            if (lispRunner == null) return false;
            return lispRunner.sourceImages != null
                && lispRunner.sourceImages.Length > 0
                && lispRunner.sourceImages[0] != null;
        }
    }
}
