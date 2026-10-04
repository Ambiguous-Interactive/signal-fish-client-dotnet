/*
    The M8.1 movement-sync sample: the owning client drives its capsule,
    the server relays the pose to every observer on FishNet's unreliable
    channel (which the bridge delivers over the reliable relay floor in
    this version — see docs/adapters/fishnet.md for the delivery story).

    The sample compiles in the project's assemblies, not this package's
    (Unity imports Samples~ into Assembly-CSharp), so it carries no
    define guard: it only compiles once FishNet itself is installed,
    which is the sample's own prerequisite. Requires the legacy Input
    backend (Input.GetAxis) — the Player settings' Active Input
    Handling must include the old Input Manager, or swap the reads for
    the Input System equivalents.
*/
namespace SignalFish.Client.Adapters.FishNet.Samples
{
    using FishNet.Object;
    using FishNet.Transporting;
    using UnityEngine;

    /// <summary>
    /// Syncs one player capsule: the owning client feeds input, the server
    /// validates nothing (sample-grade) and rebroadcasts the pose, and
    /// observers apply it verbatim. A real game interpolates between
    /// poses; this sample exists to show the wiring, not the netcode.
    /// </summary>
    public class MovementSyncPlayer : NetworkBehaviour
    {
        [SerializeField]
        [Tooltip("Units per second the capsule moves under input.")]
        private float _moveSpeed = 5f;

        [SerializeField]
        [Tooltip("Seconds between pose rebroadcasts while moving.")]
        private float _sendInterval = 0.05f;

        private float _sendTimer;

        private void Update()
        {
            if (!IsOwner)
            {
                return;
            }

            Vector3 input = new Vector3(Input.GetAxis("Horizontal"), 0f, Input.GetAxis("Vertical"));
            if (input.sqrMagnitude > 0.01f)
            {
                transform.position += input * (_moveSpeed * Time.deltaTime);
            }

            _sendTimer += Time.deltaTime;
            if (_sendTimer >= _sendInterval)
            {
                _sendTimer = 0f;
                ServerSetPose(transform.position, transform.rotation);
            }
        }

        [ServerRpc]
        private void ServerSetPose(Vector3 position, Quaternion rotation)
        {
            transform.SetPositionAndRotation(position, rotation);
            ObserverSetPose(position, rotation);
        }

        [ObserversRpc(Channel = Channel.Unreliable)]
        private void ObserverSetPose(Vector3 position, Quaternion rotation)
        {
            if (IsOwner)
            {
                return;
            }

            transform.SetPositionAndRotation(position, rotation);
        }
    }
}
