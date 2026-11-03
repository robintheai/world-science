package io.github.robintheai.worldscience;
import androidx.test.platform.app.InstrumentationRegistry;
import org.junit.Test;
import static org.junit.Assert.*;
import android.content.Intent;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.atomic.AtomicBoolean;
import org.json.*;

public final class LinkStoreTest {
 private final android.app.Instrumentation instrumentation=InstrumentationRegistry.getInstrumentation();
 private final android.content.Context context=instrumentation.getTargetContext();
 private void idle(MainActivity activity)throws Exception {var field=MainActivity.class.getDeclaredField("busy");field.setAccessible(true);for(int i=0;i<150;i++){boolean[] busy={true};instrumentation.runOnMainSync(()->{try{busy[0]=field.getBoolean(activity);}catch(Exception e){throw new RuntimeException(e);}});if(!busy[0])return;Thread.sleep(100);}fail("UI action did not finish");}
 @Test public void importRefreshAndStorage()throws Exception {
  context.getSharedPreferences("link-subscriptions",0).edit().clear().commit();
  try(ServerSocket server=new ServerSocket(0,10,InetAddress.getByName("127.0.0.1"))){
   AtomicBoolean invalid=new AtomicBoolean();Thread endpoint=new Thread(()->{while(!server.isClosed()){try(Socket socket=server.accept()){
    socket.setSoTimeout(2000);var reader=new java.io.BufferedReader(new java.io.InputStreamReader(socket.getInputStream()));String line;while((line=reader.readLine())!=null&&!line.isEmpty()){}
    String node="vless://00000000-0000-4000-8000-000000000001@10.0.2.2:19191#Japan";
    String body=invalid.get()?"<html>unavailable</html>":node+"\n"+node+"\nvless://00000000-0000-4000-8000-000000000001@10.0.2.2:19192#Germany";
    byte[] data=body.getBytes(StandardCharsets.UTF_8);socket.getOutputStream().write(("HTTP/1.1 200 OK\r\nContent-Length: "+data.length+"\r\nConnection: close\r\n\r\n").getBytes(StandardCharsets.US_ASCII));socket.getOutputStream().write(data);
   }catch(Exception ignored){}}});endpoint.setDaemon(true);endpoint.start();
   MainActivity activity=(MainActivity)instrumentation.startActivitySync(new Intent(context,MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));idle(activity);
   String url="http://127.0.0.1:"+server.getLocalPort()+"/sub?token=fixture-only";
   instrumentation.runOnMainSync(()->activity.importLink(url));idle(activity);
   assertEquals(1,LinkStore.read(context).length());Locations.update(LinkStore.config(context));assertEquals(2,Locations.all().size());
   assertFalse(context.getSharedPreferences("link-subscriptions",0).getAll().toString().contains("fixture-only"));
   instrumentation.runOnMainSync(()->activity.importLink(url));idle(activity);assertEquals(1,LinkStore.read(context).length());
   String before=LinkStore.config(context);invalid.set(true);try{LinkStore.refresh(context);fail("Invalid update accepted");}catch(Exception expected){}assertEquals(before,LinkStore.config(context));
   Locations.select(context,Locations.all().get(0));JSONObject config=new JSONObject(Locations.configure(context,before));JSONArray out=config.getJSONArray("outbounds");for(int i=0;i<out.length();i++)if(out.getJSONObject(i).optString("tag").equals("auto"))assertEquals(1,out.getJSONObject(i).getJSONArray("outbounds").length());
   instrumentation.runOnMainSync(()->{try{var f=MainActivity.class.getDeclaredField("input");f.setAccessible(true);assertEquals("",((android.widget.EditText)f.get(activity)).getText().toString());}catch(Exception e){throw new RuntimeException(e);}});
   LinkLifecycleTest.capture(context,"link-home.png");
   invalid.set(false);assertEquals(1,LinkStore.refresh(context));
   LinkStore.remove(context,0);assertEquals(0,LinkStore.read(context).length());Locations.select(context,"");
  }
 }
 @Test public void importsClashYamlAndPastedNodes()throws Exception {
  context.getSharedPreferences("link-subscriptions",0).edit().clear().commit();
  String clash="proxies:\n  - {name: Clash Japan, type: vless, server: 10.0.2.2, port: 19191, uuid: 00000000-0000-4000-8000-000000000001, tls: true, servername: example.com}\n  - {name: Clash Germany, type: trojan, server: 10.0.2.2, port: 19192, password: fixture, sni: example.com}\n  - {name: Clash Snell, type: snell, server: 10.0.2.2, port: 19193, psk: fixture}\nrules:\n  - MATCH,DIRECT\n";
  try(ServerSocket server=new ServerSocket(0,10,InetAddress.getByName("127.0.0.1"))){
   Thread endpoint=new Thread(()->{while(!server.isClosed()){try(Socket socket=server.accept()){
    socket.setSoTimeout(2000);var reader=new java.io.BufferedReader(new java.io.InputStreamReader(socket.getInputStream()));String line;while((line=reader.readLine())!=null&&!line.isEmpty()){}
    byte[] data=clash.getBytes(StandardCharsets.UTF_8);socket.getOutputStream().write(("HTTP/1.1 200 OK\r\nContent-Length: "+data.length+"\r\nConnection: close\r\n\r\n").getBytes(StandardCharsets.US_ASCII));socket.getOutputStream().write(data);
   }catch(Exception ignored){}}});endpoint.setDaemon(true);endpoint.start();
   LinkStore.add(context,"http://127.0.0.1:"+server.getLocalPort()+"/clash");
  }
  Locations.update(LinkStore.config(context));assertEquals(2,Locations.all().size());assertTrue(Locations.all().stream().anyMatch(t->Locations.country(t).equals("Clash Japan")));
  assertTrue(LinkStore.pasted(clash)&&LinkStore.pasted(java.util.Base64.getUrlEncoder().withoutPadding().encodeToString(clash.getBytes(StandardCharsets.UTF_8)))&&!LinkStore.pasted("not-a-link")&&!LinkStore.pasted("https://example.com/sub"));
  String pasted="vless://00000000-0000-4000-8000-000000000002@10.0.2.2:19194?security=tls&sni=example.com#Pasted%20A trojan://fixture@10.0.2.2:19195?sni=example.com#Pasted%20B";
  LinkStore.add(context,pasted);JSONArray saved=LinkStore.read(context);
  assertEquals(2,saved.length());assertTrue(LinkStore.manual(saved.getJSONObject(1)));assertEquals("",saved.getJSONObject(1).getString("url"));assertEquals(Text.t("Manual nodes","手动添加的节点"),LinkStore.title(saved.getJSONObject(1),1));
  LinkStore.add(context,pasted);assertEquals(2,LinkStore.read(context).length());
  Locations.update(LinkStore.config(context));assertEquals(4,Locations.all().size());
  LinkStore.add(context,java.util.Base64.getUrlEncoder().withoutPadding().encodeToString(clash.replace("19191","19196").replace("19192","19197").getBytes(StandardCharsets.UTF_8)));assertEquals(3,LinkStore.read(context).length());
  for(String bad:new String[]{"not-a-link","vless://broken"}){try{LinkStore.add(context,bad);fail(bad);}catch(Exception expected){}}
  assertEquals(3,LinkStore.read(context).length());
  LinkStore.remove(context,0);String before=LinkStore.config(context);assertEquals(0,LinkStore.refresh(context));assertEquals(before,LinkStore.config(context));
  context.getSharedPreferences("link-subscriptions",0).edit().clear().commit();Locations.select(context,"");
 }
 @Test public void rejectsUnsafeAndEmptyLinks()throws Exception {
  for(String value:new String[]{"file:///tmp/sub","https://user:pass@example.com/sub","not-a-link"}){try{LinkStore.url(value);fail(value);}catch(Exception expected){}}
  try{LinkStore.validated("<html>Unavailable</html>");fail("invalid nodes accepted");}catch(Exception expected){}
 }
 @Test public void writeSavedSubscription()throws Exception {
  // Isolated valid fixture persisted for the package reinstall test.
  context.getSharedPreferences("link-subscriptions",0).edit().clear().commit();
  JSONArray rows=new JSONArray().put(new JSONObject().put("url","https://example.invalid/sub").put("host","example.invalid").put("config",LinkStore.validated("vless://00000000-0000-4000-8000-000000000001@10.0.2.2:19191#Japan")));
  var save=LinkStore.class.getDeclaredMethod("save",android.content.Context.class,JSONArray.class);save.setAccessible(true);save.invoke(null,context,rows);assertEquals(1,LinkStore.read(context).length());
 }
 @Test public void restoreSavedSubscription()throws Exception {assertEquals(1,LinkStore.read(context).length());assertEquals("example.invalid",LinkStore.read(context).getJSONObject(0).getString("host"));assertTrue(LinkStore.config(context).contains("10.0.2.2"));}
}
