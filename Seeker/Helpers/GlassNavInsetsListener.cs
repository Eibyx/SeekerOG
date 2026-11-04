using Android.Views;
using AndroidX.Core.View;

namespace Seeker
{
    /// <summary>
    /// Keeps the floating glass bottom bar above the gesture / 3-button navigation bar:
    /// bottom margin = system navigation inset + a small gap.
    /// </summary>
    public class GlassNavInsetsListener : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        private readonly int gapPx;

        public GlassNavInsetsListener(int gapPx)
        {
            this.gapPx = gapPx;
        }

        public WindowInsetsCompat OnApplyWindowInsets(View v, WindowInsetsCompat insets)
        {
            var bars = insets.GetInsets(WindowInsetsCompat.Type.NavigationBars());
            if (v.LayoutParameters is ViewGroup.MarginLayoutParams lp)
            {
                int wanted = bars.Bottom + gapPx;
                if (lp.BottomMargin != wanted)
                {
                    lp.BottomMargin = wanted;
                    v.LayoutParameters = lp;
                }
            }
            return insets;
        }
    }
}
