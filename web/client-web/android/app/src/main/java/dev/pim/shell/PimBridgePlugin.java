package dev.pim.shell;

import android.content.SharedPreferences;
import android.util.Log;

import com.getcapacitor.JSObject;
import com.getcapacitor.Plugin;
import com.getcapacitor.PluginCall;
import com.getcapacitor.PluginMethod;
import com.getcapacitor.annotation.CapacitorPlugin;

import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;

/**
 * PimBridge 参考实现（测试壳专用）。
 *
 * 规格 03 §6：真实消费方（手机采集端 App）在自身 WebView 里注入 window.pimAndroid；
 * 本插件让 Capacitor 壳能以等价契约自测内嵌页（/embed/android/*）全链路：
 * - token.request / token.refresh → 用 configure() 存入的账号向服务器登录换取令牌
 *   （真实 App 由自己的会话提供，不落 Web localStorage）
 * - native.state.request → 返回模拟采集状态
 * - page.report → 仅日志
 */
@CapacitorPlugin(name = "PimBridge")
public class PimBridgePlugin extends Plugin {

    private static final String PREFS = "pim_shell";
    private String cachedToken;

    @PluginMethod
    public void handleMessage(PluginCall call) {
        String raw = call.getString("raw", "");
        try {
            JSONObject msg = new JSONObject(raw);
            String type = msg.optString("type");
            String requestId = msg.optString("requestId");
            switch (type) {
                case "token.request":
                case "token.refresh": {
                    final boolean refresh = "token.refresh".equals(type);
                    new Thread(() -> {
                        String token = obtainToken(refresh);
                        JSObject payload = new JSObject();
                        try {
                            if (token != null) payload.put("token", token);
                            deliver(type, requestId, payload);
                        } catch (Exception e) {
                            Log.e("PimBridge", "deliver failed", e);
                        }
                    }, "pim-bridge-token").start();
                    break;
                }
                case "native.state.request": {
                    try {
                        JSObject payload = new JSObject()
                                .put("continuousCollection", true)
                                .put("triggerReason", "测试壳模拟")
                                .put("pendingUpload", 0)
                                .put("batteryOptimized", true);
                        deliver(type, requestId, payload);
                    } catch (Exception e) {
                        Log.e("PimBridge", "state deliver failed", e);
                    }
                    break;
                }
                case "page.report":
                    Log.i("PimBridge", "page.report: " + msg);
                    break;
                default:
                    Log.w("PimBridge", "未知桥消息: " + type);
            }
            call.resolve();
        } catch (Exception e) {
            call.reject(e.getMessage() == null ? "bridge error" : e.getMessage());
        }
    }

    /** 配置测试壳的令牌来源（服务器地址 + 账号；真实 App 不需要这一步） */
    @PluginMethod
    public void configure(PluginCall call) {
        SharedPreferences prefs = bridge.getContext().getSharedPreferences(PREFS, 0);
        prefs.edit()
                .putString("server", call.getString("server", prefs.getString("server", null)))
                .putString("username", call.getString("username", prefs.getString("username", null)))
                .putString("password", call.getString("password", prefs.getString("password", null)))
                .apply();
        cachedToken = null;
        call.resolve();
    }

    private void deliver(String type, String requestId, JSObject payload) throws Exception {
        JSObject envelope = new JSObject()
                .put("type", type)
                .put("requestId", requestId)
                .put("payload", payload);
        String script = "window.__pimBridgeReceive && window.__pimBridgeReceive("
                + JSONObject.quote(envelope.toString()) + ")";
        bridge.getWebView().post(() -> bridge.getWebView().evaluateJavascript(script, null));
    }

    private String obtainToken(boolean refresh) {
        if (!refresh && cachedToken != null) return cachedToken;
        SharedPreferences prefs = bridge.getContext().getSharedPreferences(PREFS, 0);
        // prefs 未配置时回退到 strings.xml 的构建期默认值（测试壳注入）
        String server = prefs.getString("server", bridge.getContext().getString(R.string.pim_default_server));
        String user = prefs.getString("username", bridge.getContext().getString(R.string.pim_default_user));
        String pass = prefs.getString("password", bridge.getContext().getString(R.string.pim_default_pass));
        if (server == null || user == null || pass == null) {
            Log.w("PimBridge", "无法取令牌：server/username/password 均未配置");
            return null;
        }
        HttpURLConnection conn = null;
        try {
            conn = (HttpURLConnection) new URL(server.replaceAll("/+$", "") + "/api/v1/auth/login").openConnection();
            conn.setRequestMethod("POST");
            conn.setRequestProperty("Content-Type", "application/json");
            conn.setConnectTimeout(8000);
            conn.setReadTimeout(8000);
            conn.setDoOutput(true);
            try (OutputStream os = conn.getOutputStream()) {
                os.write(("{\"username\":\"" + user + "\",\"password\":\"" + pass + "\"}").getBytes(StandardCharsets.UTF_8));
            }
            InputStream in = conn.getResponseCode() < 400 ? conn.getInputStream() : conn.getErrorStream();
            StringBuilder sb = new StringBuilder();
            try (BufferedReader r = new BufferedReader(new java.io.InputStreamReader(in, StandardCharsets.UTF_8))) {
                String line;
                while ((line = r.readLine()) != null) sb.append(line);
            }
            JSONObject tokenEl = new JSONObject(sb.toString()).optJSONObject("data");
            String token = tokenEl == null ? null : tokenEl.optString("accessToken", null);
            if (token != null && !token.isEmpty()) {
                cachedToken = token;
                Log.i("PimBridge", "token obtained (" + token.length() + " chars)");
                return token;
            }
            Log.w("PimBridge", "登录响应无令牌: " + sb.substring(0, Math.min(160, sb.length())));
            return null;
        } catch (Exception e) {
            Log.e("PimBridge", "login failed", e);
            return null;
        } finally {
            if (conn != null) conn.disconnect();
        }
    }
}
