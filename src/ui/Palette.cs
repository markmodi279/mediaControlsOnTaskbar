using System.Drawing;

namespace MediaController
{
    /// <summary>
    /// The one place that knows which way "away from the background" is.
    ///
    /// Everything drawn derives from a background colour - the sampled taskbar for the tile and
    /// menu, the card's own surface for the panel - and must work whether that colour is dark
    /// or light. A light taskbar is the default on a fresh Windows 10 in light mode, and
    /// anything that lightens the background for its surfaces and paints near-white text on top
    /// is invisible there. So the two operations are stated once, here, in terms of the
    /// background rather than in terms of "lighter": Lift moves away from it whichever way that
    /// is, and text tones come from whichever pole the background is not sitting on.
    /// </summary>
    static class Palette
    {
        /// <summary>
        /// True when the background is nearer white than black.
        ///
        /// Rec. 601 luma rather than a plain channel average: green carries most of
        /// perceived brightness and blue almost none, so a saturated blue accent taskbar
        /// reads as dark - which it is, to the eye - where an average would call it
        /// middling and leave the text tone on a coin toss.
        /// </summary>
        public static bool IsLight(Color background)
        {
            return (299 * background.R + 587 * background.G + 114 * background.B) / 1000 >= 128;
        }

        /// <summary>
        /// Moves a colour away from the background by <paramref name="amount"/> of the
        /// distance to the far end - lightening a dark background, darkening a light one.
        ///
        /// Amounts are tuned against a dark background; the same number buys the same
        /// separation on a light one instead of washing out.
        /// </summary>
        public static Color Lift(Color background, float amount)
        {
            int target = IsLight(background) ? 0 : 255;

            return Color.FromArgb(Mix(background.R, target, amount),
                                  Mix(background.G, target, amount),
                                  Mix(background.B, target, amount));
        }

        // Text tones, one column per pole.
        //
        // The light column is not a straight mirror. Pure black on a near-white bar is
        // harsher than near-white is on a dark one - dark-on-light already has the higher
        // contrast at equal distance from the background - so each tone stops short of the
        // end, and the muted tone barely moves, because it is already mid-grey.
        //
        //                                        on dark   on light
        static readonly int[] Primary   = { 255, 20 };   // titles and the highest-contrast text
        static readonly int[] Body      = { 225, 45 };   // menu rows
        static readonly int[] Secondary = { 205, 75 };   // hover panel window rows
        static readonly int[] Muted     = { 130, 120 };  // a row that cannot be clicked

        /// <summary>Highest-contrast text. Titles.</summary>
        public static Color PrimaryText(Color background) { return Tone(background, Primary); }

        /// <summary>Ordinary readable text. Menu rows.</summary>
        public static Color BodyText(Color background) { return Tone(background, Body); }

        /// <summary>Text that supports something above it: an artist under a title.</summary>
        public static Color SecondaryText(Color background) { return Tone(background, Secondary); }

        /// <summary>
        /// A disabled row. Faded, deliberately not faded out: a greyed item here means the
        /// action is impossible right now, which is information worth reading.
        /// </summary>
        public static Color MutedText(Color background) { return Tone(background, Muted); }

        /// <summary>From <paramref name="a"/> to <paramref name="b"/> by t, 0..1.</summary>
        public static Color Blend(Color a, Color b, float t)
        {
            if (t <= 0f) return a;
            if (t >= 1f) return b;
            return Color.FromArgb(Mix(a.R, b.R, t), Mix(a.G, b.G, t), Mix(a.B, b.B, t));
        }

        static Color Tone(Color background, int[] pair)
        {
            return Grey(pair[IsLight(background) ? 1 : 0]);
        }

        static Color Grey(int tone)
        {
            return Color.FromArgb(tone, tone, tone);
        }

        static int Mix(int from, int to, float amount)
        {
            return Clamp(from + (int)((to - from) * amount));
        }

        static int Clamp(int v)
        {
            return v < 0 ? 0 : (v > 255 ? 255 : v);
        }
    }
}
