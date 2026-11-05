package io.github.robintheai.worldscience;
import android.app.*;
import android.content.*;
import android.graphics.*;
import android.net.*;
import android.os.*;
import android.view.*;
import android.widget.*;
import org.json.*;
import java.util.*;
import java.util.concurrent.*;

public final class MainActivity extends Activity {
 private final ExecutorService io=Executors.newSingleThreadExecutor();private final Handler ui=new Handler(Looper.getMainLooper());
 private GlassStyle glass;private LinearLayout root,home,subscriptions,saved,locationList;private ScrollView homeScroll,subScroll;private TextView status,location,download,upload,message,details;private EditText input;private Button addLink,updateNodes;private Dial dial;private boolean busy;private int savedCount;
 private int dp(float n){return (int)(getResources().getDisplayMetrics().density*n+.5f);}
 private LinearLayout column(){LinearLayout l=new LinearLayout(this);l.setOrientation(LinearLayout.VERTICAL);return l;}
 private TextView text(String value,int size,int color){TextView t=new TextView(this);t.setText(value);t.setTextSize(size);t.setTextColor(color);t.setGravity(Gravity.CENTER);return t;}
 private void add(LinearLayout l,View v,int h){l.addView(v,new LinearLayout.LayoutParams(-1,h<0?h:dp(h)));}
 private void space(LinearLayout l,int h){add(l,new View(this),h);}
 private Button button(String title,boolean primary){Button b=new Button(this);b.setText(title);b.setAllCaps(false);b.setTextSize(15);b.setTextColor(primary?glass.canvas:glass.ink);b.setBackground(glass.button(primary));b.setPadding(dp(16),dp(8),dp(16),dp(8));return b;}
 private ScrollView scroll(LinearLayout content){ScrollView s=new ScrollView(this);s.setFillViewport(true);s.setVerticalScrollBarEnabled(false);s.addView(content,new ScrollView.LayoutParams(-1,-2));root.addView(s,new LinearLayout.LayoutParams(-1,0,1));return s;}
 public void onCreate(Bundle b){
  setTheme((getResources().getConfiguration().uiMode&android.content.res.Configuration.UI_MODE_NIGHT_MASK)==android.content.res.Configuration.UI_MODE_NIGHT_YES?android.R.style.Theme_Material_NoActionBar:android.R.style.Theme_Material_Light_NoActionBar);super.onCreate(b);glass=new GlassStyle(this);
  getWindow().setStatusBarColor(glass.canvas);getWindow().setNavigationBarColor(glass.canvas);root=column();root.setPadding(dp(24),dp(12),dp(24),dp(12));root.setBackground(glass.background());setContentView(root);
  if(Build.VERSION.SDK_INT>=30){getWindow().setDecorFitsSystemWindows(false);getWindow().getInsetsController().setSystemBarsAppearance(glass.dark?0:WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS|WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS,WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS|WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS);root.setOnApplyWindowInsetsListener((v,insets)->{var bars=insets.getInsets(WindowInsets.Type.systemBars());var ime=insets.getInsets(WindowInsets.Type.ime());v.setPadding(dp(24)+bars.left,dp(12)+bars.top,dp(24)+bars.right,dp(12)+Math.max(bars.bottom,ime.bottom));return insets;});}
  add(root,text("Owlpath",21,glass.ink),52);
  home=column();homeScroll=scroll(home);dial=new Dial();add(home,dial,218);status=text(Text.state("Not connected"),25,glass.ink);add(home,status,42);location=text(Text.t("Automatic location","自动选择线路"),14,glass.muted);add(home,location,30);space(home,16);
  Button choose=button(Text.t("◎   Choose location   ⌄","◎   选择线路   ⌄"),false);choose.setOnClickListener(v->toggleLocations());add(home,choose,52);locationList=column();locationList.setPadding(dp(8),dp(8),dp(8),dp(8));locationList.setBackground(glass.surface(false));add(home,locationList,-2);locationList.setVisibility(View.GONE);space(home,12);
  updateNodes=button(Text.t("Update nodes","更新节点"),false);updateNodes.setOnClickListener(v->refreshNodes());add(home,updateNodes,48);space(home,22);
  LinearLayout meters=new LinearLayout(this);LinearLayout down=column(),up=column();add(down,text(Text.t("↓  Download","↓  下载"),12,glass.muted),26);download=text("—",20,glass.ink);add(down,download,32);add(up,text(Text.t("↑  Upload","↑  上传"),12,glass.muted),26);upload=text("—",20,glass.ink);add(up,upload,32);meters.addView(down,new LinearLayout.LayoutParams(0,-2,1));meters.addView(up,new LinearLayout.LayoutParams(0,-2,1));add(home,meters,-2);space(home,28);
  Button manage=button(Text.t("Subscriptions   ›","我的订阅   ›"),false);manage.setOnClickListener(v->showSubscriptions());add(home,manage,54);details=text("",13,glass.muted);add(home,details,-2);space(home,12);
  Button coffee=button(Text.t("Buy me a coffee","请作者喝杯咖啡"),false);coffee.setOnClickListener(v->{try{startActivity(new Intent(Intent.ACTION_VIEW,Uri.parse("https://ai.z5a.com/support/coffee")));}catch(ActivityNotFoundException e){showMessage(Text.t("Open ai.z5a.com/support/coffee in your browser.","请在浏览器打开 ai.z5a.com/support/coffee"));}});add(home,coffee,48);
  subscriptions=column();subScroll=scroll(subscriptions);Button back=button(Text.t("‹  Back","‹  返回"),false);back.setOnClickListener(v->{if(savedCount>0)showHome();});add(subscriptions,back,48);space(subscriptions,24);add(subscriptions,text(Text.t("Your subscriptions","我的订阅"),25,glass.ink),48);add(subscriptions,text(Text.t("Paste a subscription link or node links. No sign-in needed.","粘贴订阅链接或节点链接即可使用，无需注册登录。"),14,glass.muted),54);
  input=new EditText(this);input.setSingleLine(false);input.setMaxLines(4);input.setHint(Text.t("https://… or vmess://, ss://, Clash","https://… 或 vmess://、ss://、Clash 配置"));input.setTextSize(16);input.setTextColor(glass.ink);input.setHintTextColor(glass.muted);input.setInputType(android.text.InputType.TYPE_CLASS_TEXT|android.text.InputType.TYPE_TEXT_VARIATION_URI|android.text.InputType.TYPE_TEXT_FLAG_MULTI_LINE|android.text.InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);input.setImportantForAutofill(View.IMPORTANT_FOR_AUTOFILL_NO);input.setSaveEnabled(false);input.setPadding(dp(16),dp(12),dp(16),dp(12));input.setBackground(glass.surface(false));input.setContentDescription("Subscription URL");input.setMinHeight(dp(58));add(subscriptions,input,-2);space(subscriptions,14);
  addLink=button(Text.t("Add subscription","添加订阅"),true);addLink.setOnClickListener(v->importLink(input.getText().toString()));add(subscriptions,addLink,54);space(subscriptions,24);saved=column();add(subscriptions,saved,-2);
  message=text("",13,glass.muted);message.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE);message.setPadding(0,dp(8),0,dp(8));add(root,message,-2);message.setVisibility(View.GONE);showSubscriptions();run(Text.t("Loading subscriptions…","正在读取订阅…"),()->load(false));ui.post(tick);
 }
 interface Job{void run()throws Exception;}
 private void run(String progress,Job job){if(busy){showMessage(Text.t("Please wait for the current request to finish.","请等当前操作完成。"));return;}busy=true;showMessage(progress);updateButtons();io.execute(()->{try{job.run();}catch(Exception e){String m=e.getMessage();ui.post(()->showMessage(m!=null&&m.length()<180&&!m.contains("://")?m:Text.t("Unable to complete this request. Try again.","操作失败，请重试。")));}finally{ui.post(()->{busy=false;updateButtons();});}});}
 private void updateButtons(){addLink.setEnabled(!busy);addLink.setText(busy?Text.t("Please wait…","请稍候…"):Text.t("Add subscription","添加订阅"));updateNodes.setEnabled(!busy);updateNodes.setText(busy?Text.t("Updating…","正在更新…"):Text.t("Update nodes","更新节点"));dial.setEnabled(!busy||!"Not connected".equals(TunnelService.state));}
 private void showMessage(String value){message.setText(value);message.setVisibility(value.isEmpty()?View.GONE:View.VISIBLE);}
 private void load(boolean stayOnHome)throws Exception {
  JSONArray list=LinkStore.read(this);List<String> hosts=new ArrayList<>(),usages=new ArrayList<>();int manual=0;for(int i=0;i<list.length();i++){JSONObject item=list.getJSONObject(i);if(LinkStore.manual(item))manual++;hosts.add(LinkStore.title(item,LinkStore.manual(item)?manual:0));usages.add(LinkStore.usageSummary(item));}
  boolean lost=LinkStore.resetAfterKeyLoss;LinkStore.resetAfterKeyLoss=false;
  if(list.length()>0)Locations.update(LinkStore.config(this));else Locations.update("{\"outbounds\":[]}");
  if(!Locations.selected(this).isEmpty()&&!Locations.all().contains(Locations.selected(this)))Locations.select(this,"");
  ui.post(()->{savedCount=hosts.size();saved.removeAllViews();for(int i=0;i<hosts.size();i++){final int index=i;Button entry=button(hosts.get(i)+(usages.get(i).isEmpty()?"":"\n"+usages.get(i))+"   ·   "+Text.t("Remove","删除"),false);entry.setOnClickListener(v->confirmRemove(index));add(saved,entry,-2);entry.setMinimumHeight(dp(52));space(saved,8);}String usage=usages.isEmpty()?"":usages.get(0);details.setText(Text.t(savedCount+" saved · "+Locations.all().size()+(Locations.all().size()==1?" location":" locations"),savedCount+" 个订阅 · "+Locations.all().size()+" 条线路")+(usage.isEmpty()?"":"\n"+usage));showMessage(lost?Text.t("Saved subscriptions could not be read on this phone. Please add your link again.","这台手机上的已存订阅无法读取，请重新添加订阅链接。"):"");if(stayOnHome||savedCount>0)showHome();else showSubscriptions();});
 }
 void importLink(String url){run(Text.t("Downloading subscription…","正在下载订阅…"),()->{LinkStore.add(this,url);load(true);ui.post(()->{input.setText("");((android.view.inputmethod.InputMethodManager)getSystemService(INPUT_METHOD_SERVICE)).hideSoftInputFromWindow(input.getWindowToken(),0);});});}
 void refreshNodes(){run(Text.t("Updating nodes…","正在更新节点…"),()->{int n=LinkStore.refresh(this),remote=LinkStore.remote(LinkStore.read(this));load(true);ui.post(()->showMessage(remote==0?Text.t("Manually added nodes do not need updating.","手动添加的节点无需更新。"):n==remote?Text.t("Nodes updated.","节点已更新。"):Text.t("Some subscriptions could not be updated. Saved nodes were kept.","部分订阅更新失败，已保留原有节点。")));});}
 private void confirmRemove(int index){if(busy)return;new AlertDialog.Builder(this).setTitle(Text.t("Remove subscription?","删除这个订阅？")).setMessage(Text.t("Its saved locations will be removed. An active connection will stop.","它的所有线路都会被删除，正在使用的连接也会断开。")).setNegativeButton(Text.t("Cancel","取消"),null).setPositiveButton(Text.t("Remove","删除"),(d,w)->run(Text.t("Removing subscription…","正在删除订阅…"),()->{stopTunnel();LinkStore.remove(this,index);load(false);})).show();}
 private void stopTunnel()throws Exception {if(!"Not connected".equals(TunnelService.state)){startService(new Intent(this,TunnelService.class).setAction("STOP"));for(int i=0;i<100&&!"Not connected".equals(TunnelService.state);i++)Thread.sleep(100);if(!"Not connected".equals(TunnelService.state))throw new Exception(Text.t("Wait for the connection to stop, then try again.","请等连接断开后再试。"));}}
 private void showHome(){subScroll.setVisibility(View.GONE);homeScroll.setVisibility(View.VISIBLE);}
 private void showSubscriptions(){homeScroll.setVisibility(View.GONE);subScroll.setVisibility(View.VISIBLE);}
 private void toggleLocations(){if(locationList.getVisibility()==View.VISIBLE){locationList.setVisibility(View.GONE);return;}locationList.removeAllViews();LinearLayout rows=column();List<String> choices=new ArrayList<>();choices.add("");choices.addAll(Locations.all());for(String node:choices){Button b=button((node.isEmpty()?Text.t("Automatic","自动选择"):Locations.country(node))+(node.equals(Locations.selected(this))?"  ✓":""),false);b.setTextSize(13);b.setOnClickListener(v->{Locations.select(this,node);locationList.setVisibility(View.GONE);if(!"Not connected".equals(TunnelService.state))startService(new Intent(this,TunnelService.class).setAction("SELECT_COUNTRY"));});add(rows,b,48);}ScrollView scroll=new ScrollView(this);scroll.addView(rows);add(locationList,scroll,Math.min(240,choices.size()*48));locationList.setVisibility(View.VISIBLE);}
 private void toggle(){if("Disconnecting…".equals(TunnelService.state))return;if(!"Not connected".equals(TunnelService.state)){startService(new Intent(this,TunnelService.class).setAction("STOP"));return;}if(savedCount==0){showSubscriptions();return;}if(busy)return;showMessage("");
  // Android 13+ hides the connection notification (and its Disconnect button) unless notifications are allowed.
  if(Build.VERSION.SDK_INT>=33&&checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS)!=android.content.pm.PackageManager.PERMISSION_GRANTED)requestPermissions(new String[]{android.Manifest.permission.POST_NOTIFICATIONS},11);
  Intent consent=android.net.VpnService.prepare(this);if(consent!=null)startActivityForResult(consent,10);else startForegroundService(new Intent(this,TunnelService.class));}
 protected void onActivityResult(int request,int result,Intent data){super.onActivityResult(request,result,data);if(request==10){if(result==RESULT_OK)startForegroundService(new Intent(this,TunnelService.class));else showMessage(Text.t("Allow the VPN connection to connect.","需要允许 VPN 连接请求才能连接。"));}}
 private String rate(double n){return n>=1e6?String.format(Locale.US,"%.1f MB/s",n/1e6):String.format(Locale.US,"%.1f KB/s",Math.max(0,n)/1000);}
 private final Runnable tick=new Runnable(){public void run(){String s=TunnelService.state;status.setText(Text.state(s));location.setText(Locations.label(MainActivity.this));download.setText("—");upload.setText("—");try{if("Connected".equals(s)){JSONObject data=new JSONObject(TunnelService.stats);download.setText(rate(data.optDouble("down")));upload.setText(rate(data.optDouble("up")));}}catch(Exception ignored){}if(!busy&&!TunnelService.error.isEmpty())showMessage(TunnelService.error);dial.invalidate();ui.postDelayed(this,1000);}};
 protected void onDestroy(){ui.removeCallbacks(tick);io.shutdown();super.onDestroy();}
 private final class Dial extends View {
  final Paint p=new Paint(3);long pulse;
  Dial(){super(MainActivity.this);setContentDescription(Text.t("Connect or disconnect","连接或断开"));setOnClickListener(v->toggle());setFocusable(true);setClickable(true);setLayerType(View.LAYER_TYPE_SOFTWARE,null);}
  // The same owl as the desktop connect button. Disconnected: daylight, eyes shut. Connecting: dusk, eyes half open,
  // a soft pulse. Connected: night, the moon behind the owl, amber eyes that blink every few seconds.
  final Path body=new Path(),beak=new Path(),shut=new Path();
  {body.moveTo(184,150);body.cubicTo(212,170,236,178,256,178);body.cubicTo(276,178,300,170,328,150);body.cubicTo(342,186,350,218,348,252);body.cubicTo(372,296,376,360,344,398);body.cubicTo(320,426,290,434,256,434);body.cubicTo(222,434,192,426,168,398);body.cubicTo(136,360,140,296,164,252);body.cubicTo(162,218,170,186,184,150);body.close();
   beak.moveTo(244,304);beak.lineTo(268,304);beak.lineTo(256,324);beak.close();shut.moveTo(192,260);shut.quadTo(214,280,236,260);shut.moveTo(276,260);shut.quadTo(298,280,320,260);}
  protected void onDraw(Canvas c){
   String state=TunnelService.state;boolean night="Connected".equals(state),dusk=!night&&!"Not connected".equals(state);
   boolean motion=android.provider.Settings.Global.getFloat(getContentResolver(),android.provider.Settings.Global.ANIMATOR_DURATION_SCALE,1f)>0;
   float x=getWidth()/2f,y=getHeight()/2f,r=dp(84),s=2*r;long now=SystemClock.uptimeMillis();
   p.setStyle(Paint.Style.FILL);p.setShadowLayer(dp(18),0,dp(8),glass.dark?0x55000000:0x180B1528);p.setColor(0xFF0D0E11);c.drawCircle(x,y,r,p);p.clearShadowLayer();
   p.setShader(night?new RadialGradient(x,y,r,0xFF2C2E35,0xFF0D0E11,Shader.TileMode.CLAMP):dusk?new LinearGradient(x,y-r,x,y+r,new int[]{0xFF343A7C,0xFF8A5EA0,0xFFF2A877},new float[]{0,.55f,1},Shader.TileMode.CLAMP):new RadialGradient(x,y,r,0xFFFFFFFF,0xFFEFEFEA,Shader.TileMode.CLAMP));c.drawCircle(x,y,r,p);p.setShader(null);
   if(night||dusk){float my=y-r+s*(night?.39f:.46f);p.setShader(new RadialGradient(x-s*.04f,my-s*.05f,s*.25f,0xFFFFF4D2,0xFFF1D88E,Shader.TileMode.CLAMP));p.setAlpha(night?242:204);c.drawCircle(x,my,s*.25f,p);p.setShader(null);p.setAlpha(255);}
   p.setStyle(Paint.Style.STROKE);p.setStrokeWidth(dp(1));p.setColor(glass.edge);c.drawCircle(x,y,r,p);
   float k=s*.40f/296;c.save();c.translate(x,y-r+s*.15f);c.scale(k,k);c.translate(-256,-144);
   int bodyColor=night?0xFF0B0D16:dusk?0xFF2A2D5E:0xFF55658F;p.setColor(bodyColor);p.setStyle(Paint.Style.FILL_AND_STROKE);p.setStrokeWidth(12);p.setStrokeJoin(Paint.Join.ROUND);c.drawPath(body,p);
   float pulse=dusk&&motion?(float)(.5+.5*Math.sin(now/250.0)):.5f,glow=night?.6f:dusk?.15f+.25f*pulse:0;
   if(glow>0){p.setStyle(Paint.Style.FILL);p.setShader(new RadialGradient(256,262,140,0xF2FFC94D,0x00FFC94D,Shader.TileMode.CLAMP));p.setAlpha((int)(255*glow));c.save();c.scale(1,76/140f,256,262);c.drawCircle(256,262,140,p);c.restore();p.setShader(null);p.setAlpha(255);}
   long phase=now%6500;float blink=night&&motion&&phase<240?(float)Math.sin(Math.PI*phase/240):0,open=(night?1:dusk?.45f:0)*(1-.92f*blink);
   p.setStyle(Paint.Style.FILL);
   if(open>.02f)for(float[] e:new float[][]{{214,218,208},{298,294,288}}){p.setColor(0xFFFFB52E);c.drawOval(e[0]-36,262-36*open,e[0]+36,262+36*open,p);p.setColor(0xFF0E1838);c.drawOval(e[1]-15,265-15*open,e[1]+15,265+15*open,p);if(open>.6f){p.setColor(0xFFFFF3C4);p.setAlpha((int)(255*(open-.6f)/.4f));c.drawCircle(e[2],254,6,p);p.setAlpha(255);}}
   if(!night&&!dusk){p.setStyle(Paint.Style.STROKE);p.setStrokeWidth(9);p.setStrokeCap(Paint.Cap.ROUND);p.setColor(0xFFD5DCF2);c.drawPath(shut,p);}
   p.setStyle(Paint.Style.FILL_AND_STROKE);p.setStrokeWidth(8);p.setColor(0xFFE59A2F);c.drawPath(beak,p);c.restore();
   p.setStyle(Paint.Style.FILL);p.setColor(night||dusk?0xFFF4F1E8:0xFF3A4566);p.setTextAlign(Paint.Align.CENTER);p.setTextSize(14*getResources().getDisplayMetrics().scaledDensity);String label="Not connected".equals(TunnelService.state)?Text.t("Connect","连接"):"Connected".equals(TunnelService.state)?Text.t("Disconnect","断开"):Text.t("Cancel","取消");c.drawText(label,x,y+r-dp(14),p);
   if(dusk&&motion||night&&motion&&phase<240)postInvalidateOnAnimation();else if(night&&motion)postInvalidateDelayed(6500-phase);
  }
 }
}
