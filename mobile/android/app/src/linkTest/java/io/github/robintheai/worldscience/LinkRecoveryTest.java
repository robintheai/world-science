package io.github.robintheai.worldscience;

import android.content.Intent;
import android.net.*;
import androidx.test.platform.app.InstrumentationRegistry;
import io.github.robintheai.worldscience.mobilecore.Mobilecore;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import org.json.*;
import org.junit.Test;
import static org.junit.Assert.*;

/** Exercises the real service and native engine; only a local fixture endpoint is used. */
public final class LinkRecoveryTest {
 private final android.content.Context context=InstrumentationRegistry.getInstrumentation().getTargetContext();
 private void foreground() {
  InstrumentationRegistry.getInstrumentation().startActivitySync(new Intent(context,MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
  assertNull("Fixture VPN consent required",VpnService.prepare(context));
 }
 private void start(String config){context.startForegroundService(new Intent(context,TunnelService.class).setAction("TEST_CONNECT").putExtra("fixture",config));}
 private boolean hasVpn(){
  var cm=context.getSystemService(ConnectivityManager.class);
  for(Network n:cm.getAllNetworks()){var c=cm.getNetworkCapabilities(n);if(c!=null&&c.hasTransport(NetworkCapabilities.TRANSPORT_VPN))return true;}
  return false;
 }
 private void stop() throws Exception {
  context.startService(new Intent(context,TunnelService.class).setAction("STOP"));
  for(int i=0;i<40;i++){Thread.sleep(250);if(!hasVpn()&&"Not connected".equals(TunnelService.state))return;}
  fail("Disconnect did not finish: "+TunnelService.state);
 }
 @Test public void failedStartupNeverClaimsConnected() throws Exception {
  foreground();
  try {
   start("{\"outbounds\":[{\"type\":\"invalid-fixture-protocol\"}]}");
   // Cross at least one scheduled recovery tick. Direct Internet must not count as VPN health.
   for(int i=0;i<40;i++){
    Thread.sleep(500);
    assertNotEquals("Failed engine must not appear connected", "Connected",TunnelService.state);
    assertFalse("Failed startup must not leak a VPN interface",hasVpn());
   }
  } finally {stop();}
 }
 @Test public void repeatedConnectDisconnectAndLateSelection() throws Exception {
  foreground();
  var config=new JSONObject(Mobilecore.buildConfig("vless://00000000-0000-4000-8000-000000000001@10.0.2.2:19191#Fixture"));
  var route=config.getJSONObject("route");var rules=new JSONArray().put(new JSONObject().put("ip_cidr",new JSONArray().put("198.18.0.2/32")).put("action","route").put("outbound","auto"));
  var old=route.getJSONArray("rules");for(int i=0;i<old.length();i++)rules.put(old.get(i));route.put("rules",rules);
  for(int cycle=0;cycle<3;cycle++){
   try {
    start(config.toString());boolean received=false;
    for(int i=0;i<20&&!received;i++){
     Thread.sleep(500);
     try(var socket=new Socket()){
      socket.connect(new java.net.InetSocketAddress("198.18.0.2",18080),1000);socket.setSoTimeout(2000);
      socket.getOutputStream().write("GET / HTTP/1.1\r\nHost: fixture.invalid\r\nConnection: close\r\n\r\n".getBytes(StandardCharsets.US_ASCII));
      received=new String(socket.getInputStream().readAllBytes(),StandardCharsets.US_ASCII).contains("ws-vpn-tunnel-fixture-ok");
     }catch(Exception ignored){}
    }
    assertTrue("Reconnect cycle "+cycle+" must pass real TUN traffic",received);
    context.startService(new Intent(context,TunnelService.class).setAction("STOP"));
    context.startService(new Intent(context,TunnelService.class).setAction("SELECT_COUNTRY"));
   } finally {stop();}
   assertEquals("Late location selection must not restart a stopped VPN","Not connected",TunnelService.state);
  }
 }
}
