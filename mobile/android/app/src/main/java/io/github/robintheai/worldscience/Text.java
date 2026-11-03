package io.github.robintheai.worldscience;
import java.util.Locale;

/** Interface text follows the phone language. Service states stay English identifiers internally. */
final class Text {
 private Text(){}
 static boolean zh(){return "zh".equals(Locale.getDefault().getLanguage());}
 static String t(String en,String zh){return zh()?zh:en;}
 static String state(String s){
  switch(s){
   case "Connected":return t(s,"已连接");
   case "Connecting…":return t(s,"正在连接…");
   case "Reconnecting…":return t(s,"正在重连…");
   case "Disconnecting…":return t(s,"正在断开…");
   case "Waiting for network…":return t(s,"等待网络恢复…");
   default:return t("Not connected","未连接");
  }
 }
}
