package io.github.robintheai.worldscience;

import android.content.Context;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;
import io.github.robintheai.worldscience.mobilecore.Mobilecore;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.security.*;
import java.util.*;
import javax.crypto.*;
import javax.crypto.spec.GCMParameterSpec;
import org.json.*;

/** Subscription credentials remain in encrypted, app-private storage. No account API. */
final class LinkStore {
 private static final String ALIAS=BuildConfig.STORE_ALIAS;
 private static javax.crypto.SecretKey key() throws Exception {
  KeyStore ks=KeyStore.getInstance("AndroidKeyStore");ks.load(null);
  if(!ks.containsAlias(ALIAS)){KeyGenerator g=KeyGenerator.getInstance("AES","AndroidKeyStore");g.init(new KeyGenParameterSpec.Builder(ALIAS,KeyProperties.PURPOSE_ENCRYPT|KeyProperties.PURPOSE_DECRYPT).setBlockModes("GCM").setEncryptionPaddings("NoPadding").build());g.generateKey();}
  return (javax.crypto.SecretKey)ks.getKey(ALIAS,null);
 }
 /** Set when saved subscriptions could not be decrypted (for example after a device restore) and were reset. */
 static volatile boolean resetAfterKeyLoss;
 static synchronized JSONArray read(Context c)throws Exception {
  var p=c.getSharedPreferences("link-subscriptions",0);String data=p.getString("data",null);if(data==null)return new JSONArray();
  try{
   Cipher cipher=Cipher.getInstance("AES/GCM/NoPadding");cipher.init(Cipher.DECRYPT_MODE,key(),new GCMParameterSpec(128,Base64.decode(p.getString("iv",""),Base64.NO_WRAP)));
   return new JSONArray(new String(cipher.doFinal(Base64.decode(data,Base64.NO_WRAP)),StandardCharsets.UTF_8));
  }catch(Exception lost){
   // Only unrecoverable data/key states reset the store; other failures propagate unchanged.
   // Transient keystore errors (KeyStoreException, a busy keystore daemon) must never erase saved links.
   if(!(lost instanceof AEADBadTagException||lost instanceof android.security.keystore.KeyPermanentlyInvalidatedException||lost instanceof UnrecoverableKeyException||lost instanceof IllegalArgumentException||lost instanceof JSONException))throw lost;
   // An unreadable store would otherwise block every future "Add subscription". Start clean and ask to re-add.
   p.edit().remove("data").remove("iv").commit();
   try{KeyStore ks=KeyStore.getInstance("AndroidKeyStore");ks.load(null);ks.deleteEntry(ALIAS);}catch(Exception ignored){}
   resetAfterKeyLoss=true;return new JSONArray();
  }
 }
 private static synchronized void save(Context c,JSONArray a)throws Exception {
  Cipher cipher=Cipher.getInstance("AES/GCM/NoPadding");cipher.init(Cipher.ENCRYPT_MODE,key());byte[] encrypted=cipher.doFinal(a.toString().getBytes(StandardCharsets.UTF_8));
  if(!c.getSharedPreferences("link-subscriptions",0).edit().putString("iv",Base64.encodeToString(cipher.getIV(),Base64.NO_WRAP)).putString("data",Base64.encodeToString(encrypted,Base64.NO_WRAP)).commit())throw new Exception("Unable to save subscriptions.");
 }
 static URI url(String text)throws Exception {
  try{URI u=new URI(text.trim()).normalize();if(!Set.of("http","https").contains(u.getScheme())||u.getHost()==null||u.getRawUserInfo()!=null||u.getFragment()!=null)throw new Exception();return u;}
  catch(Exception e){throw new Exception(Text.t("Enter a valid HTTP or HTTPS subscription link, or paste node links.","请输入完整的 HTTP 或 HTTPS 订阅链接，或粘贴节点链接。"));}
 }
 private static final java.util.regex.Pattern SHARE=java.util.regex.Pattern.compile("(?i)(?<![a-z0-9+.-])(?:vmess|vless|trojan|ss|hysteria2|hy2|tuic|anytls)://\\S"),CLASH=java.util.regex.Pattern.compile("(?m)^proxies:");
 /** Pasted text that holds nodes itself (share links, base64, Clash YAML or SIP008 JSON) rather than a subscription address. */
 static boolean pasted(String text){
  String t=text.trim();if(t.isEmpty())return false;
  if(SHARE.matcher(t).find()||CLASH.matcher(t).find()||t.startsWith("{")&&t.contains("\"servers\"")||t.startsWith("[")&&t.contains("\"server\""))return true;
  for(int flags:new int[]{Base64.DEFAULT,Base64.URL_SAFE}){try{String d=StandardCharsets.UTF_8.newDecoder().decode(java.nio.ByteBuffer.wrap(Base64.decode(t,flags))).toString();if(SHARE.matcher(d).find()||CLASH.matcher(d).find())return true;}catch(Exception ignored){}}
  return false;
 }
 /** Pasted nodes are kept as a local entry with no address; refresh never downloads them. */
 static boolean manual(JSONObject item){return item.optBoolean("manual")||item.optString("url").isEmpty();}
 static String title(JSONObject item,int number){return manual(item)?Text.t("Manual nodes","手动添加的节点")+(number>1?" "+number:""):item.optString("host");}
 private static volatile android.net.Network bypass;
 /** Downloads directly over Wi-Fi/mobile data when the tunnel's own nodes are dead. */
 static String fetch(Context c,URI uri)throws Exception {
  try{return fetch(uri);}
  catch(Exception tunnelFailure){
   var cm=c.getSystemService(android.net.ConnectivityManager.class);
   for(var n:cm.getAllNetworks()){var caps=cm.getNetworkCapabilities(n);if(caps!=null&&caps.hasCapability(android.net.NetworkCapabilities.NET_CAPABILITY_NOT_VPN)&&caps.hasCapability(android.net.NetworkCapabilities.NET_CAPABILITY_INTERNET)){bypass=n;try{return fetch(uri);}catch(Exception ignored){}finally{bypass=null;}}}
   throw tunnelFailure;
  }
 }
 /** Last subscription-userinfo header seen by fetch on this thread. */
 private static final ThreadLocal<String> usage=new ThreadLocal<>();
 private static String fetch(URI uri)throws Exception {
  long deadline=android.os.SystemClock.elapsedRealtime()+30000;URI current=uri;
  for(int redirects=0;redirects<=5;redirects++){
   HttpURLConnection connection=null;
   try{
    var route=bypass;connection=(HttpURLConnection)(route!=null?route.openConnection(current.toURL()):current.toURL().openConnection());connection.setConnectTimeout(8000);connection.setReadTimeout(8000);connection.setInstanceFollowRedirects(false);connection.setRequestProperty("User-Agent","v2rayN/7.0 WorldScience/1.0");
    int status=connection.getResponseCode();
    if(status>=300&&status<400){String location=connection.getHeaderField("Location");if(location==null)throw new Exception();URI next=url(current.resolve(location).toString());if("https".equals(current.getScheme())&&!"https".equals(next.getScheme()))throw new Exception();current=next;continue;}
    if(status!=200||connection.getContentLengthLong()>8*1024*1024)throw new Exception();
    usage.set(connection.getHeaderField("subscription-userinfo"));
    try(var in=connection.getInputStream();var out=new java.io.ByteArrayOutputStream()){
     byte[] b=new byte[32768];int n;while((n=in.read(b))!=-1){if(Thread.currentThread().isInterrupted()||android.os.SystemClock.elapsedRealtime()>deadline)throw new Exception();if(out.size()+n>8*1024*1024)throw new Exception();out.write(b,0,n);}return out.toString("UTF-8");
    }
   }catch(Exception e){throw new Exception(Text.t("Unable to download the subscription. Check the link and try again.","订阅下载失败，请检查链接后重试。"));}
   finally{if(connection!=null)connection.disconnect();}
  }
  throw new Exception(Text.t("Too many subscription redirects.","订阅链接跳转次数过多。"));
 }
 static synchronized void add(Context c,String text)throws Exception {
  URI u;try{u=url(text);}catch(Exception notLink){if(!pasted(text))throw notLink;addPasted(c,text);return;}
  JSONArray a=read(c);for(int i=0;i<a.length();i++)if(a.getJSONObject(i).getString("url").equals(u.toString()))return;
  if(a.length()>=20)throw new Exception(Text.t("You can save up to 20 subscriptions.","最多保存 20 个订阅。"));
  usage.remove();String config=validated(fetch(c,u));a.put(new JSONObject().put("url",u.toString()).put("host",u.getHost()).put("config",config).put("usage",usage.get()==null?"":usage.get()));combined(a);save(c,a);
 }
 private static synchronized void addPasted(Context c,String text)throws Exception {
  if(text.getBytes(StandardCharsets.UTF_8).length>8*1024*1024)throw new Exception(Text.t("This subscription is too large to import.","订阅内容过大，无法导入。"));
  StringBuilder digest=new StringBuilder("sha256:");for(byte b:MessageDigest.getInstance("SHA-256").digest(text.trim().getBytes(StandardCharsets.UTF_8)))digest.append(String.format(Locale.ROOT,"%02x",b));
  JSONArray a=read(c);for(int i=0;i<a.length();i++)if(digest.toString().equals(a.getJSONObject(i).optString("digest")))return;
  if(a.length()>=20)throw new Exception(Text.t("You can save up to 20 subscriptions.","最多保存 20 个订阅。"));
  String config=validated(text);a.put(new JSONObject().put("url","").put("host","").put("manual",true).put("digest",digest.toString()).put("config",config).put("usage",""));combined(a);save(c,a);
 }
 static int remote(JSONArray a)throws Exception {int n=0;for(int i=0;i<a.length();i++)if(!manual(a.getJSONObject(i)))n++;return n;}
 /** @return subscriptions downloaded; pasted entries are skipped and never count as failures. */
 static synchronized int refresh(Context c)throws Exception {
  JSONArray a=read(c);int succeeded=0;long deadline=android.os.SystemClock.elapsedRealtime()+45000;if(remote(a)==0)return 0;
  for(int i=0;i<a.length();i++){if(Thread.currentThread().isInterrupted()||android.os.SystemClock.elapsedRealtime()>deadline)break;JSONObject item=a.getJSONObject(i);if(manual(item))continue;try{usage.remove();String config=validated(fetch(c,url(item.getString("url"))));item.put("config",config);if(usage.get()!=null)item.put("usage",usage.get());succeeded++;}catch(Exception ignored){}}
  if(succeeded==0)throw new Exception(Text.t("Unable to update nodes. Your saved nodes are still available.","节点更新失败，原有节点仍可使用。"));
  combined(a);save(c,a);return succeeded;
 }
 static synchronized void remove(Context c,int index)throws Exception {JSONArray a=read(c);a.remove(index);save(c,a);}
 static String validated(String links)throws Exception {try{return Mobilecore.buildConfig(links);}catch(Exception e){throw new Exception(Text.t("No supported nodes found. Use a subscription link, node links or a Clash config.","没有找到可用的节点，请使用订阅链接、节点链接或 Clash 配置。"));}}
 /** "剩余 120 GB · 2026-12-01 到期" from a saved subscription-userinfo header, or "". */
 static String usageSummary(JSONObject item){
  String header=item.optString("usage","");if(header.isEmpty())return "";
  long up=0,down=0,total=0,expire=0;
  for(String part:header.split("[;,]")){String[] kv=part.trim().split("=",2);if(kv.length!=2)continue;long v;try{v=(long)Double.parseDouble(kv[1].trim());}catch(NumberFormatException e){continue;}
   switch(kv[0].trim().toLowerCase(Locale.ROOT)){case "upload":up=v;break;case "download":down=v;break;case "total":total=v;break;case "expire":expire=v;break;default:}}
  List<String> parts=new ArrayList<>();
  if(total>0){long left=Math.max(0,total-up-down);parts.add(Text.t("Left "+bytes(left)+" of "+bytes(total),"剩余 "+bytes(left)+" / 共 "+bytes(total)));}
  if(expire>0){String day=new java.text.SimpleDateFormat("yyyy-MM-dd",Locale.ROOT).format(new Date(expire*1000));parts.add(expire*1000<System.currentTimeMillis()?Text.t("expired","已到期"):Text.t("expires "+day,day+" 到期"));}
  return String.join(" · ",parts);
 }
 private static String bytes(long n){return n>=1L<<40?String.format(Locale.US,"%.2f TB",n/(double)(1L<<40)):n>=1L<<30?String.format(Locale.US,"%.1f GB",n/(double)(1L<<30)):String.format(Locale.US,"%.0f MB",n/(double)(1L<<20));}
 static String config(Context c)throws Exception {return combined(read(c));}
 private static String canonical(Object o)throws Exception {
  if(o instanceof JSONObject){JSONObject j=(JSONObject)o;TreeSet<String> names=new TreeSet<>();j.keys().forEachRemaining(names::add);StringBuilder b=new StringBuilder("{");for(String n:names)b.append(JSONObject.quote(n)).append(':').append(canonical(j.get(n))).append(',');return b.append('}').toString();}
  if(o instanceof JSONArray){JSONArray a=(JSONArray)o;StringBuilder b=new StringBuilder("[");for(int i=0;i<a.length();i++)b.append(canonical(a.get(i))).append(',');return b.append(']').toString();}return String.valueOf(o);
 }
 static String combined(JSONArray subscriptions)throws Exception {
  if(subscriptions.length()==0)throw new Exception(Text.t("Add a subscription to get started.","请先添加订阅链接。"));
  JSONObject config=null;JSONArray out=new JSONArray(),members=new JSONArray();Set<String> seen=new HashSet<>();
  for(int n=0;n<subscriptions.length();n++){
   JSONObject parsed=new JSONObject(subscriptions.getJSONObject(n).getString("config"));if(config==null)config=parsed;JSONArray nodes=parsed.getJSONArray("outbounds");
   for(int i=0;i<nodes.length();i++){JSONObject node=nodes.getJSONObject(i);if(!node.has("server"))continue;String title=node.optString("tag").replaceFirst("\\s*·\\s*\\d+$","");node.remove("tag");
    String id=Base64.encodeToString(MessageDigest.getInstance("SHA-256").digest(canonical(node).getBytes(StandardCharsets.UTF_8)),Base64.NO_WRAP|Base64.URL_SAFE);if(!seen.add(id))continue;
    String tag=title+" · "+node.optString("type")+" · "+id.substring(0,8);node.put("tag",tag);out.put(node);members.put(tag);
   }
  }
  if(members.length()==0)throw new Exception(Text.t("No supported nodes found.","没有找到可用的节点。"));
  out.put(new JSONObject().put("type","urltest").put("tag","auto").put("outbounds",members).put("url","https://cp.cloudflare.com/generate_204").put("interval","1m").put("tolerance",80).put("interrupt_exist_connections",false));
  out.put(new JSONObject().put("type","direct").put("tag","direct"));config.put("outbounds",out);
  // Account-service bypasses do not belong in this independent subscription client.
  for(String section:new String[]{"route","dns"}){JSONObject part=config.getJSONObject(section);JSONArray rules=part.getJSONArray("rules"),kept=new JSONArray();for(int i=0;i<rules.length();i++){JSONObject rule=rules.getJSONObject(i);if(!rule.has("domain"))kept.put(rule);}part.put("rules",kept);}
  return config.toString();
 }
}
