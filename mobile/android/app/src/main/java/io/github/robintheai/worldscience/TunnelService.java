package io.github.robintheai.worldscience;
import android.app.*;
import android.content.*;
import android.net.*;
import android.os.*;
import io.github.robintheai.worldscience.mobilecore.*;
import org.json.*;
import java.net.NetworkInterface;
import java.util.Collections;
import java.util.concurrent.*;

public final class TunnelService extends VpnService implements Native {
 static volatile boolean needsSignIn;
 static volatile String state="Not connected",error="",stats="{}";
 private ScheduledExecutorService worker;
 private final Engine engine=Mobilecore.newEngine();
 private ParcelFileDescriptor tun;
 private final Object lifecycle=new Object();
 private final java.util.concurrent.atomic.AtomicBoolean closing=new java.util.concurrent.atomic.AtomicBoolean();
 private volatile boolean wanted,tunnelReady,pendingStart,networkTracked;
 private volatile java.net.HttpURLConnection activeProbe;
 private volatile Network underlying;
 private ConnectivityManager connectivity;
 private int failures,ticks,fallbackChecks;
 // The chosen location failed, so the fastest other one carries traffic until the chosen one answers again.
 private volatile boolean fallback;
 private String lastNetwork="",shownState="";
 private ScheduledFuture<?> networkUpdate;
 private String testConfig;
 private final Handler main=new Handler(Looper.getMainLooper());
 // requestNetwork tracks the system's best non-VPN network; getAllNetworks() order could pick cellular while on Wi-Fi.
 private final ConnectivityManager.NetworkCallback network=new ConnectivityManager.NetworkCallback(){
  public void onAvailable(Network n){underlying=n;applyUnderlying();notifyNetwork();}
  public void onLost(Network n){if(n.equals(underlying)){underlying=null;applyUnderlying();}notifyNetwork();}
  public void onLinkPropertiesChanged(Network n,LinkProperties p){notifyNetwork();}
 };
 private void applyUnderlying(){try{Network n=underlying;setUnderlyingNetworks(n==null?null:new Network[]{n});}catch(Exception ignored){}}
 private synchronized void notifyNetwork(){
  if(!wanted||worker==null||worker.isShutdown())return;
  if(networkUpdate!=null)networkUpdate.cancel(false);
  try{networkUpdate=worker.schedule(()->{
   if(!wanted)return;
   String key=networkKey();
   if(!key.equals(lastNetwork)){boolean initialized=!lastNetwork.isEmpty();lastNetwork=key;if(initialized)engine.networkChanged();}
  },1,TimeUnit.SECONDS);}catch(RejectedExecutionException ignored){}
 }
 private String networkKey(){
  Network n=underlying;
  if(connectivity==null||n==null)return "offline";
  var p=connectivity.getLinkProperties(n);
  return p==null?"offline":n+":"+p.getLinkAddresses();
 }
 private void execute(Runnable action){
  if(worker==null||worker.isShutdown())return;
  try{worker.execute(action);}catch(RejectedExecutionException ignored){}
 }
 private Notification notification(){
  var open=PendingIntent.getActivity(this,0,new Intent(this,MainActivity.class),PendingIntent.FLAG_IMMUTABLE);
  var stop=PendingIntent.getService(this,1,new Intent(this,TunnelService.class).setAction("STOP"),PendingIntent.FLAG_IMMUTABLE);
  String detail="Connected".equals(state)?Text.state(state)+" · "+Locations.label(this):Text.state(state);
  return new Notification.Builder(this,"vpn").setSmallIcon(io.github.robintheai.worldscience.R.drawable.notify).setContentTitle("World Science").setContentText(detail)
   .setContentIntent(open).setOngoing(true).setOnlyAlertOnce(true)
   .addAction(new Notification.Action.Builder(null,Text.t("Disconnect","断开"),stop).build()).build();
 }
 private void refreshNotification(){
  String now=state+"|"+Locations.label(this);
  if(now.equals(shownState)||!wanted)return;shownState=now;
  try{getSystemService(NotificationManager.class).notify(1,notification());}catch(Exception ignored){}
 }
 public int onStartCommand(Intent intent,int flags,int id){
  if(intent!=null&&"STOP".equals(intent.getAction())){pendingStart=false;disconnect();return START_NOT_STICKY;}
  if(intent!=null&&"SELECT_COUNTRY".equals(intent.getAction())){
   if(wanted&&!closing.get())execute(()->{if(!wanted)return;state="Connecting…";fallback=false;error="";try{connect();}catch(Exception e){if(wanted){error=Text.t("Selected location is unavailable. Choose another location.","所选线路暂不可用，请换一条。");state="Reconnecting…";}}});
   return START_NOT_STICKY;
  }
  if(BuildConfig.DEBUG && intent!=null && "TEST_CONNECT".equals(intent.getAction()))testConfig=intent.getStringExtra("fixture");
  // A foreground-service start must always reach startForeground, even while a previous stop is finishing;
  // otherwise Android kills the app with ForegroundServiceDidNotStartInTimeException.
  var nm=getSystemService(NotificationManager.class);nm.createNotificationChannel(new NotificationChannel("vpn",Text.t("Connection","连接状态"),NotificationManager.IMPORTANCE_LOW));
  if(!wanted&&!closing.get()){state="Connecting…";error="";}
  startForeground(1,notification());
  if(closing.get()){pendingStart=true;return START_NOT_STICKY;}
  if(wanted)return START_NOT_STICKY;
  begin();
  return START_NOT_STICKY;
 }
 private void begin(){
  wanted=true;needsSignIn=false;state="Connecting…";error="";failures=0;ticks=0;fallback=false;shownState="";
  worker=Executors.newSingleThreadScheduledExecutor();
  connectivity=getSystemService(ConnectivityManager.class);
  // If tracking cannot be registered, health checks still run; "offline" is only trusted when tracking works.
  try{connectivity.requestNetwork(new NetworkRequest.Builder().addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET).addCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN).build(),network);networkTracked=true;}catch(Exception e){networkTracked=false;}
  execute(()->{try{connect();}catch(Exception e){if(!wanted)return;if(BuildConfig.DEBUG)android.util.Log.e("WorldScience-test","VPN startup failed",e);error=Text.t("Could not connect. Check your subscription or choose another location.","连接失败。请检查订阅是否有效，或换一条线路。");disconnect();}});
  try{worker.scheduleWithFixedDelay(this::maintenanceTick,1,1,TimeUnit.SECONDS);}catch(RejectedExecutionException ignored){}
 }
 private boolean interactive(){try{return getSystemService(PowerManager.class).isInteractive();}catch(Exception e){return true;}}
 private void maintenanceTick(){
   if(!wanted)return;
   try{
    boolean screen=interactive();
    if(screen||ticks%10==0)stats=engine.status();
    refreshNotification();
    // Probe every 15 s with the screen on and every 2 minutes with it off, to save battery.
    if(++ticks%(screen?15:120)!=0)return;
    // Offline: keep the tunnel and wait. Rebuilding it cannot help and leaks traffic during each rebuild.
    if(networkTracked&&underlying==null&&testConfig==null){state="Waiting for network…";failures=0;return;}
    boolean healthy=tunnelReady&&probe();if(!wanted)return;
    if(healthy){
     failures=0;state="Connected";if(!fallback)error="";
     // About every 3 minutes with the screen on, see whether the chosen location answers again.
     if(fallback&&++fallbackChecks%12==0&&probeChosen()){fallback=false;error="";connect();}
    }
    else if(!tunnelReady||++failures>=3){
     state="Reconnecting…";failures=0;
     // A chosen location that keeps failing gives way to the others; if they fail too, the chosen one is tried again.
     if(testConfig==null&&!Locations.selected(this).isEmpty()){fallback=!fallback;fallbackChecks=0;error=fallback?Text.t("Your chosen location is unavailable, so the fastest working one is used for now. It switches back when it recovers.","所选线路暂不可用，已临时改用最快的可用线路，恢复后自动切回。"):"";}
     connect();
    }
   }catch(Exception e){
    if(!wanted)return;
    // Startup / failed recovery stays wanted; the next scheduled probe retries it.
   }
 }
 private void closeTun(){try{if(tun!=null)tun.close();}catch(Exception ignored){}finally{tun=null;}}
 private void connect()throws Exception{
  String config=testConfig!=null?testConfig:Locations.configure(this,LinkStore.config(this),fallback);
  synchronized(lifecycle){
   if(!wanted)return;
   // Keep the existing VPN interface across engine restarts: while no engine runs, traffic is
   // dropped instead of falling back to the physical network. Only disconnect() removes it.
   tunnelReady=false;try{engine.stop();}finally{if(!wanted)closeTun();}
   if(!wanted)return;
   try{
    if(tun==null)openTun();
    engine.start(config,getFilesDir().getAbsolutePath(),this);
    if(tun==null||!tun.getFileDescriptor().valid())throw new IllegalStateException("VPN interface unavailable");
    tunnelReady=true;lastNetwork=networkKey();applyUnderlying();
    if(!wanted){tunnelReady=false;try{engine.stop();}finally{closeTun();}return;}
   }catch(Exception failure){tunnelReady=false;try{engine.stop();}finally{if(!wanted)closeTun();}throw failure;}
  }
  boolean healthy=probe();if(wanted)state=healthy?"Connected":"Reconnecting…";
 }
 private boolean probeChosen(){
  java.net.HttpURLConnection connection=null;
  try{
   connection=(java.net.HttpURLConnection)new java.net.URL(Locations.CHECK_URL).openConnection();activeProbe=connection;
   connection.setConnectTimeout(5000);connection.setReadTimeout(5000);connection.setInstanceFollowRedirects(false);
   return wanted&&tunnelReady&&connection.getResponseCode()==204;
  }catch(Exception e){return false;}finally{if(connection!=null)connection.disconnect();if(activeProbe==connection)activeProbe=null;}
 }
 private boolean probe(){
  // Ordinary Internet access must never masquerade as a working VPN after failed startup.
  if(!wanted||!tunnelReady)return false;
  for(String host:new String[]{"https://cp.cloudflare.com/generate_204","https://www.google.com/generate_204"}){
   if(!wanted||!tunnelReady)return false;
   java.net.HttpURLConnection connection=null;
   try{
    connection=(java.net.HttpURLConnection)new java.net.URL(host).openConnection();activeProbe=connection;
    connection.setConnectTimeout(5000);connection.setReadTimeout(5000);connection.setInstanceFollowRedirects(false);
    if(!wanted||!tunnelReady)return false;
    if(connection.getResponseCode()==204)return wanted&&tunnelReady;
   }catch(Exception ignored){}finally{if(connection!=null)connection.disconnect();if(activeProbe==connection)activeProbe=null;}
  }
  return false;
 }
 public int openTun()throws Exception{if(prepare(this)!=null)throw new Exception("VPN permission required");Builder b=new Builder().setSession("World Science").setMtu(1400).addAddress("172.19.0.1",30).addAddress("fdfe:dcba:9876::1",126).addRoute("0.0.0.0",0).addRoute("::",0).addDnsServer("172.19.0.2");if(Build.VERSION.SDK_INT>=29)b.setMetered(false);Network n=underlying;if(n!=null)b.setUnderlyingNetworks(new Network[]{n});ParcelFileDescriptor previous=tun,next=b.establish();if(next==null)throw new Exception("VPN unavailable");tun=next;
  // The new interface replaces the previous one before it is released, so there is no unprotected gap.
  if(previous!=null)try{previous.close();}catch(Exception ignored){}
  return tun.getFd();}
 public void protectSocket(int fd)throws Exception{if(!super.protect((int)fd))throw new Exception("Socket protection failed");}
 public boolean isApple(){return false;}
 public String defaultInterface(){
  Network n=underlying;
  if(connectivity==null||n==null)return "";
  var p=connectivity.getLinkProperties(n);
  return p==null||p.getInterfaceName()==null?"":p.getInterfaceName();
 }
 public String interfaces(){JSONArray a=new JSONArray();try{for(var n:Collections.list(NetworkInterface.getNetworkInterfaces())){if(!n.isUp()||n.isLoopback())continue;JSONArray ips=new JSONArray();for(var i:n.getInterfaceAddresses())ips.put(i.getAddress().getHostAddress().split("%")[0]+"/"+i.getNetworkPrefixLength());a.put(new JSONObject().put("name",n.getName()).put("index",n.getIndex()).put("mtu",n.getMTU()).put("addresses",ips).put("type",n.getName().startsWith("wlan")?1:2));}}catch(Exception ignored){}return a.toString();}
 // The system binds an active VpnService: stopSelf alone does not invoke onDestroy.
 // Close the tunnel explicitly, independently of health-check DNS/socket waits.
 private void disconnect(){
  wanted=false;tunnelReady=false;if(!closing.compareAndSet(false,true))return;state="Disconnecting…";
  try{if(connectivity!=null&&networkTracked)connectivity.unregisterNetworkCallback(network);}catch(Exception ignored){}
  underlying=null;networkTracked=false;
  if(worker!=null)worker.shutdownNow();
  new Thread(()->{var probe=activeProbe;try{if(probe!=null)probe.disconnect();}catch(Exception ignored){}synchronized(lifecycle){try{engine.stop();}finally{closeTun();stats="{}";
   // A Connect tapped during cleanup restarts on this same service instance instead of being lost.
   main.post(()->{closing.set(false);if(pendingStart){pendingStart=false;begin();}else{state="Not connected";stopForeground(STOP_FOREGROUND_REMOVE);stopSelf();}});}}},"vpn-cleanup").start();
 }
 public void onRevoke(){
  // Another VPN app took over, or the user turned this VPN off in system settings.
  pendingStart=false;error=Text.t("Another VPN took over, or the VPN was turned off in system settings.","VPN 已在系统设置中关闭，或被其他 VPN 应用接管。");disconnect();
 }
 public void onDestroy(){pendingStart=false;disconnect();super.onDestroy();}
}
