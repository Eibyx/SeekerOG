using Android.App;
using Android.Views;
using Google.Android.Material.BottomSheet;

namespace Seeker
{
    /// <summary>
    /// Shared factory for SeekerOG glass bottom sheets: transparent window so the rounded glass
    /// background of the layout is what you see, expanded by default, real blur behind on
    /// Android 12+ when enabled (GlassAppearance), dim fallback elsewhere.
    /// </summary>
    public static class GlassSheet
    {
        public static BottomSheetDialog Create(Activity activity, View content, float maxHeightFraction = 0.92f, bool fixedHeight = false)
        {
            var dialog = new BottomSheetDialog(activity, Resource.Style.SeekerOG_BottomSheetDialog);
            int screenH = activity.Resources!.DisplayMetrics!.HeightPixels;
            int max = (int)(screenH * maxHeightFraction);
            if (fixedHeight)
            {
                content.LayoutParameters = new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MatchParent, max);
                dialog.SetContentView(content, content.LayoutParameters);
            }
            else
            {
                dialog.SetContentView(content);
            }
            // keep sticky bottom actions above the gesture / 3-button navigation bar
            int basePadding = content.PaddingBottom;
            AndroidX.Core.View.ViewCompat.SetOnApplyWindowInsetsListener(content, new BottomInsetListener(basePadding));
            dialog.ShowEvent += (s, e) =>
            {
                var sheet = dialog.FindViewById<View>(Resource.Id.design_bottom_sheet);
                if (sheet != null)
                {
                    sheet.SetBackgroundColor(Android.Graphics.Color.Transparent);
                    var behavior = BottomSheetBehavior.From(sheet);
                    behavior.MaxHeight = max;
                    behavior.SkipCollapsed = true;
                    behavior.State = BottomSheetBehavior.StateExpanded;
                }
                if (dialog.Window != null)
                {
                    GlassAppearance.ApplyBlurBehind(dialog.Window);
                }
            };
            return dialog;
        }

        private class BottomInsetListener : Java.Lang.Object, AndroidX.Core.View.IOnApplyWindowInsetsListener
        {
            private readonly int basePadding;
            public BottomInsetListener(int basePadding) { this.basePadding = basePadding; }
            public AndroidX.Core.View.WindowInsetsCompat OnApplyWindowInsets(View v, AndroidX.Core.View.WindowInsetsCompat insets)
            {
                var bars = insets.GetInsets(AndroidX.Core.View.WindowInsetsCompat.Type.NavigationBars());
                v.SetPadding(v.PaddingLeft, v.PaddingTop, v.PaddingRight, basePadding + bars.Bottom);
                return insets;
            }
        }
    }
}
