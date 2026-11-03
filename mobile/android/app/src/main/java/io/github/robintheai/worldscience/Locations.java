package io.github.robintheai.worldscience;
import android.content.Context;
import org.json.*;
import java.util.*;
final class Locations {
 private static volatile List<String> nodes=Collections.emptyList();
 static List<String> all(){return nodes;}
 static String selected(Context c){return c.getSharedPreferences("locations",0).getString("node","");}
 static void select(Context c,String node){c.getSharedPreferences("locations",0).edit().putString("node",node).apply();}
 /** Combined tags are "node name · protocol · id"; only the node name is shown. */
 static String country(String tag){return tag.replaceFirst("(?: · [A-Za-z0-9]+)? · [A-Za-z0-9_-]{8}$","");}
 static String label(Context c){String s=selected(c);return s.isEmpty()?Text.t("Automatic location","自动选择线路"):country(s);}
 static void update(String config)throws Exception {List<String> found=new ArrayList<>();JSONArray out=new JSONObject(config).getJSONArray("outbounds");for(int i=0;i<out.length();i++){JSONObject o=out.getJSONObject(i);if(o.has("server"))found.add(o.getString("tag"));}nodes=Collections.unmodifiableList(found);}
 /** Only the chosen location carries this address, so its recovery can be checked while the others carry everything else. */
 static final String CHECK_URL="http://cp.cloudflare.com:8080/generate_204";
 /** With fallback, the fastest of the other locations carries traffic while the chosen one is down. */
 static String configure(Context c,String config,boolean fallback)throws Exception {
  update(config);String chosen=selected(c);if(chosen.isEmpty())return config;
  // A refreshed subscription can rename or drop the chosen node. Connecting automatically beats failing.
  if(!nodes.contains(chosen)){select(c,"");TunnelService.error=Text.t("Your chosen location is gone, so the fastest available one is used.","原来选的线路已失效，已自动连接最快的线路。");return config;}
  JSONArray group=new JSONArray();for(String n:nodes)if(!fallback||!n.equals(chosen))group.put(n);
  if(!fallback||group.length()==0)group=new JSONArray().put(chosen);
  JSONObject json=new JSONObject(config);JSONArray out=json.getJSONArray("outbounds");for(int i=0;i<out.length();i++){JSONObject o=out.getJSONObject(i);if("auto".equals(o.optString("tag")))o.put("outbounds",group);}
  if(fallback)json.getJSONObject("route").getJSONArray("rules").put(new JSONObject().put("domain",new JSONArray().put("cp.cloudflare.com")).put("port",new JSONArray().put(8080)).put("action","route").put("outbound",chosen));
  return json.toString();
 }
}
