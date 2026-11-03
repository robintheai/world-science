package io.github.robintheai.worldscience;
import androidx.test.platform.app.InstrumentationRegistry;
import org.junit.Test;
import static org.junit.Assert.*;
import android.content.Intent;
import android.net.*;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import io.github.robintheai.worldscience.mobilecore.Mobilecore;
/** No user account or real subscription is involved in this isolated VPN data-plane test. */
public final class LinkLifecycleTest {
 private android.app.Instrumentation getInstrumentation(){return InstrumentationRegistry.getInstrumentation();}
 @Test public void testTunnelAndCleanup() throws Exception {
  var context=getInstrumentation().getTargetContext();
  var activity=getInstrumentation().startActivitySync(new Intent(context,MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
  assertNull("VPN consent must be granted to the fixture package",VpnService.prepare(context));
  String config=Mobilecore.buildConfig("vless://00000000-0000-4000-8000-000000000001@10.0.2.2:19191#United%20States");
  // Reserved test address is explicitly routed through the fixture proxy.
  // Production public-route and IPv6 policy are covered separately by core tests.
  var json=new org.json.JSONObject(config);var route=json.getJSONObject("route");var original=route.getJSONArray("rules");var rules=new org.json.JSONArray();
  rules.put(new org.json.JSONObject().put("ip_cidr",new org.json.JSONArray().put("198.18.0.2/32")).put("action","route").put("outbound","auto"));
  for(int i=0;i<original.length();i++)rules.put(original.get(i));route.put("rules",rules);config=json.toString();
  context.startForegroundService(new Intent(context,TunnelService.class).setAction("TEST_CONNECT").putExtra("fixture",config));
  boolean received=false;
  try {
   for(int i=0;i<30&&!received;i++){
    Thread.sleep(1000);
    try(Socket socket=new Socket()){
     socket.connect(new java.net.InetSocketAddress("198.18.0.2",18080),2000);socket.setSoTimeout(3000);
     socket.getOutputStream().write("GET / HTTP/1.1\r\nHost: fixture.invalid\r\nConnection: close\r\n\r\n".getBytes(StandardCharsets.US_ASCII));
     String response=new String(socket.getInputStream().readAllBytes(),StandardCharsets.US_ASCII);
     received=response.contains("ws-vpn-tunnel-fixture-ok");
    }catch(Exception ignored){}
   }
   assertTrue("Traffic must cross Android TUN and the VLESS endpoint: "+TunnelService.error,received);
   Thread.sleep(1500);capture(context,"connection-preview.png");
  } finally { context.startService(new Intent(context,TunnelService.class).setAction("STOP")); }
  var cm=context.getSystemService(ConnectivityManager.class);
  boolean vpn=true;
  for(int i=0;i<20&&vpn;i++){Thread.sleep(500);vpn=false;for(Network n:cm.getAllNetworks()){var c=cm.getNetworkCapabilities(n);if(c!=null&&c.hasTransport(NetworkCapabilities.TRANSPORT_VPN))vpn=true;}}
  assertFalse("Disconnect must remove the VPN interface",vpn);
 }
 static void capture(android.content.Context context,String name) throws Exception {
  var bitmap=InstrumentationRegistry.getInstrumentation().getUiAutomation().takeScreenshot();assertNotNull("UI screenshot",bitmap);
  try(var out=new java.io.FileOutputStream(new java.io.File(context.getExternalFilesDir(null),name))){assertTrue(bitmap.compress(android.graphics.Bitmap.CompressFormat.PNG,100,out));}
 }
}
