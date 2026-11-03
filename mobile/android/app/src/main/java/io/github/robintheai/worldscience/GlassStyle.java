package io.github.robintheai.worldscience;
import android.content.Context;
import android.content.res.Configuration;
import android.graphics.*;
import android.graphics.drawable.*;

/** Neutral translucent surfaces, drawn once by Android. No blur of text or continuous effects. */
final class GlassStyle {
 final boolean dark;final int ink,muted,canvas,edge;private final Context context;
 GlassStyle(Context c){context=c;dark=(c.getResources().getConfiguration().uiMode&Configuration.UI_MODE_NIGHT_MASK)==Configuration.UI_MODE_NIGHT_YES;ink=dark?0xFFF2F3F5:0xFF202124;muted=dark?0xFFADB2BD:0xFF626771;canvas=dark?0xFF13161C:0xFFF2F3F7;edge=dark?0xFF454B56:0xFFDADDE3;}
 int dp(float v){return (int)(v*context.getResources().getDisplayMetrics().density+.5f);}
 GradientDrawable surface(boolean selected){GradientDrawable d=new GradientDrawable(GradientDrawable.Orientation.TL_BR,dark?new int[]{selected?0xFF414956:0xEE333943,0xEE20252E}:new int[]{selected?0xFFE0E5EE:0xF9FFFFFF,0xDFE9EDF4});d.setCornerRadius(dp(24));d.setStroke(dp(1),edge);return d;}
 android.graphics.drawable.Drawable button(boolean primary){
  GradientDrawable fill=primary?new GradientDrawable():surface(false);if(primary){fill.setColor(ink);fill.setCornerRadius(dp(24));}
  return new RippleDrawable(android.content.res.ColorStateList.valueOf(dark?0x25FFFFFF:0x16000000),fill,null);
 }
 GradientDrawable background(){return new GradientDrawable(GradientDrawable.Orientation.TL_BR,dark?new int[]{0xFF181C25,0xFF12151B,0xFF202630}:new int[]{0xFFF8F9FC,0xFFEDF0F6,0xFFE3E8F0});}
}
