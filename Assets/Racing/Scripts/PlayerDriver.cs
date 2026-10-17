using UnityEngine;

namespace Racing
{
    /// <summary>
    /// One finger anywhere on the screen, like racing_unity (RaceManager.UpdatePlayerSideDragInput / UpdateSwipeDirection):
    /// <list type="bullet">
    /// <item>hold + drag = floating joystick; the drag splits into sideways maneuver and boost/brake by |dx| : |dy|,
    /// intensity = distance / (40 % of screen width); it acts only once the hold is past the 0.8 s swipe window;</item>
    /// <item>quick flick (released within 0.8 s, longer than that distance) = dash in the flick direction.</item>
    /// </list>
    /// Mouse works the same in the editor; keyboard is a test fallback (arrows/WASD, Space/X dash forward/back, Q/E side).
    /// </summary>
    public class PlayerDriver : IDriver
    {
        readonly RaceManager _race;

        public bool Dragging { get; private set; }
        public Vector2 DragStart { get; private set; }
        public Vector2 DragCurrent { get; private set; }
        public float Threshold => Mathf.Max(40f, Screen.width * _race.control.swipeScreenFraction);
        public System.Func<Vector2, bool> IgnoreTouchAt; // HUD buttons

        float _dragStartTime;
        Vector2 _pendingDash;

        public PlayerDriver(RaceManager race) => _race = race;

        /// <summary>Read the pointer every frame (Update), so no tap or flick is lost between physics steps.</summary>
        public void UpdatePointer()
        {
            bool down, held, up;
            Vector2 pos;
            if (Input.touchCount > 0)
            {
                var touch = Input.GetTouch(0);
                pos = touch.position;
                down = touch.phase == TouchPhase.Began;
                up = touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled;
                held = !up;
            }
            else
            {
                pos = Input.mousePosition;
                down = Input.GetMouseButtonDown(0);
                held = Input.GetMouseButton(0);
                up = Input.GetMouseButtonUp(0);
            }

            if (down && !Dragging && (IgnoreTouchAt == null || !IgnoreTouchAt(pos)))
            {
                Dragging = true;
                DragStart = DragCurrent = pos;
                _dragStartTime = Time.time;
            }
            if (!Dragging) return;
            DragCurrent = pos;
            if (up || !held)
            {
                Dragging = false;
                if (IsSwipe(Time.time - _dragStartTime, DragCurrent - DragStart, Threshold, _race.control.dashWindow))
                    _pendingDash = (DragCurrent - DragStart).normalized;
            }
        }

        public DriverInput GetInput(float dt)
        {
            var input = new DriverInput { holding = Dragging };
            // racing_unity: the first 0.8 s of a touch is only a possible swipe (dash) - no boost / brake / shift until the
            // joystick shows up, otherwise every upward flick also boosted.
            if (Dragging && Time.time - _dragStartTime > _race.control.dashWindow)
            {
                var (side, boostBrake) = Decompose(DragCurrent - DragStart, Threshold);
                input.side = side;
                input.boostBrake = boostBrake;
            }

            // Keyboard fallback for testing in the editor.
            float kx = (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow) ? 1f : 0f) - (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow) ? 1f : 0f);
            float ky = (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow) ? 1f : 0f) - (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow) ? 1f : 0f);
            if (kx != 0f || ky != 0f) { input.holding = true; input.side = kx; input.boostBrake = ky; }
            if (Input.GetKey(KeyCode.Space)) _pendingDash = Vector2.up;
            if (Input.GetKey(KeyCode.X)) _pendingDash = Vector2.down;
            if (Input.GetKey(KeyCode.Q)) _pendingDash = Vector2.left;
            if (Input.GetKey(KeyCode.E)) _pendingDash = Vector2.right;

            input.dash = _pendingDash;
            _pendingDash = Vector2.zero;
            return input;
        }

        /// <summary>racing_unity split: overall intensity by distance, shared between the axes by |dx| : |dy|.</summary>
        public static (float side, float boostBrake) Decompose(Vector2 delta, float threshold)
        {
            float ax = Mathf.Abs(delta.x), ay = Mathf.Abs(delta.y), total = ax + ay;
            if (total <= 0f) return (0f, 0f);
            float intensity = Mathf.Clamp01(delta.magnitude / threshold);
            return (Mathf.Sign(delta.x) * intensity * ax / total, Mathf.Sign(delta.y) * intensity * ay / total);
        }

        public static bool IsSwipe(float duration, Vector2 delta, float threshold, float window) =>
            duration <= window && delta.magnitude > threshold;
    }
}
