using System;

namespace MediaController
{
    /// <summary>
    /// The easing the panel's slide and fade is stepped by: exponential smoothing against
    /// measured elapsed time.
    /// </summary>
    static class Motion
    {
        /// <summary>
        /// Below this, a 0..1 value can no longer change an 8-bit colour, so the ease
        /// finishes there rather than spending frames converging invisibly.
        /// </summary>
        public const float ToneEpsilon = 1f / 255f;

        /// <summary>
        /// The fraction of the remaining distance to cross in a frame of
        /// <paramref name="elapsedMs"/>, for a settle time of
        /// <paramref name="animationMs"/>.
        ///
        /// Exponential smoothing rather than a fixed-duration tween, for one reason:
        /// retargeting mid-flight is free. Closing the panel
        /// while it is still opening needs no special case: it simply heads for the new target
        /// from wherever it had got to, where a tween would have to re-base. It also
        /// decelerates into place by construction, which is what the motion should do.
        ///
        /// animationMs is the time to settle, not a time constant: ln(255) time constants
        /// is where the remaining distance falls under ToneEpsilon and the ease finishes.
        /// The snap itself lands on whichever frame comes after that.
        ///
        /// A frame that arrives very late gives a rate at or near 1 and lands the value on
        /// its target, so a busy machine loses frames rather than slowing the animation
        /// down - which is exactly what stepping by elapsed time is for. Late frames are
        /// the normal case on a busy machine, not the pathological one.
        /// </summary>
        public static float Rate(int elapsedMs, int animationMs)
        {
            if (animationMs <= 0 || elapsedMs <= 0) return 1f;

            const double Settles = 5.5413;   // Math.Log(255)

            double tau = animationMs / Settles;
            return (float)(1.0 - Math.Exp(-elapsedMs / tau));
        }

        /// <summary>
        /// One step of one value toward its target. True when the value moved, which is
        /// the caller's cue both to repaint and to keep the timer running.
        ///
        /// Within <paramref name="epsilon"/> the value is snapped onto the target and the
        /// snap still counts as a move, so the last frame drawn is the exact one; the call
        /// after that finds them equal, reports nothing, and that is what stops the timer.
        /// </summary>
        public static bool Ease(ref float value, float target, float rate, float epsilon)
        {
            if (value == target) return false;

            float distance = target - value;
            if (distance < 0) distance = -distance;

            if (distance < epsilon) { value = target; return true; }

            value += (target - value) * rate;
            return true;
        }
    }
}
