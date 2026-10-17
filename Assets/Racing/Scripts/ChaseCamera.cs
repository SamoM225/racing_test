using UnityEngine;

namespace Racing
{
    /// <summary>
    /// Top-down race camera, same rig as racing_unity's VehicleCamera: it frames a point 20 m ahead of the car on the
    /// track (keeping the car's lateral offset), sits 30 m above and 40 m behind that point, pitched down 50°, FOV 60,
    /// smoothed with Lerp(3 x dt). Slow orbit over the track in the menu.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class ChaseCamera : MonoBehaviour
    {
        public CarController target;
        public float cameraHeight = 30f; // new name: the scene still holds the old chase-cam "height"
        public float distanceForward = -40f;
        public float angle = 50f;
        public float smoothSpeed = 3f;
        public float lookAhead = 20f;
        public float fieldOfView = 60f;

        Camera _cam;
        RaceManager _race;
        Vector3? _lastTarget;
        float _orbit, _shake;
        Vector3 _shakeOffset;
        CarController _target;
        int _trackIdx;

        void LateUpdate() => Follow(Time.deltaTime);

        /// <summary>One camera update (dt = 0 snaps straight to the pose).</summary>
        public void Follow(float dt)
        {
            if (_cam == null) { _cam = GetComponent<Camera>(); _cam.farClipPlane = Mathf.Max(_cam.farClipPlane, 1500f); }
            if (_race == null) _race = FindFirstObjectByType<RaceManager>();

            if (target == null || _race == null || _race.Track == null)
            {
                _lastTarget = null;
                _orbit += Time.unscaledDeltaTime * 6f;
                Vector3 center = Vector3.zero;
                float extent = 200f;
                if (_race != null && _race.Track != null && _race.Track.Center != null)
                {
                    var b = new Bounds(_race.Track.Center[0], Vector3.zero);
                    foreach (var p in _race.Track.Center) b.Encapsulate(p);
                    center = b.center;
                    extent = Mathf.Max(b.extents.x, b.extents.z);
                }
                transform.position = center + Quaternion.Euler(0f, _orbit, 0f) * new Vector3(0f, extent * 1.3f, -extent * 1.3f);
                transform.LookAt(center);
                return;
            }

            _cam.fieldOfView = fieldOfView;
            var rc = _race.Cars.Find(c => c.Car == target);
            Vector3 carPos = target.transform.position; // interpolated every frame
            Vector3 lookAt = carPos + target.transform.forward * lookAhead;
            if (rc != null)
            {
                // Track position from the interpolated car, not the 50 Hz race state - otherwise the whole view steps.
                if (_target != target) { _trackIdx = rc.TrackIndex; _target = target; }
                _race.Track.Locate(carPos, ref _trackIdx, out float d, out float lateral);
                _race.Track.Sample(d + lookAhead, out var center, out var right, out _, out _);
                lookAt = center + right * lateral; // the upcoming curve point, at the car's own offset
            }

            float k = dt <= 0f ? 1f : Mathf.Clamp01(smoothSpeed * dt);
            if (_lastTarget == null || dt <= 0f)
            {
                Place(lookAt, carPos, 1f);
                _lastTarget = lookAt;
                return;
            }
            Vector3 t = Vector3.Lerp(lookAt, _lastTarget.Value, 0.2f);
            transform.position -= transform.rotation * _shakeOffset; // last frame's shake out before smoothing
            _shakeOffset = Vector3.zero;
            Place(t, carPos, k);
            _lastTarget = t;
            if (_shake > 0.01f) // hits shake the view; the offset is not fed back into the smoothing
            {
                float time = Time.unscaledTime * 25f;
                _shakeOffset = new Vector3(Mathf.PerlinNoise(time, 0f) - 0.5f, Mathf.PerlinNoise(0f, time) - 0.5f, 0f) * _shake;
                transform.position += transform.rotation * _shakeOffset;
                _shake = Mathf.MoveTowards(_shake, 0f, dt * 3f);
            }
        }

        /// <summary>Kick the camera (m of shake, fades in ~0.3 s).</summary>
        public void Shake(float amount) => _shake = Mathf.Max(_shake, Mathf.Min(amount, 1.5f));

        void Place(Vector3 lookAt, Vector3 carPos, float k)
        {
            Vector3 forward = Vector3.ProjectOnPlane(lookAt - carPos, Vector3.up);
            forward = forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward;
            Vector3 desired = lookAt + forward * distanceForward + Vector3.up * cameraHeight;
            transform.position = Vector3.Lerp(transform.position, desired, k);
            Quaternion look = Quaternion.LookRotation((lookAt - transform.position).normalized);
            transform.rotation = Quaternion.Lerp(transform.rotation, Quaternion.Euler(angle, look.eulerAngles.y, 0f), k);
        }
    }
}
