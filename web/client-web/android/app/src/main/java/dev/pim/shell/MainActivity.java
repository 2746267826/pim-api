package dev.pim.shell;

import android.os.Bundle;
import com.getcapacitor.BridgeActivity;

public class MainActivity extends BridgeActivity {
    @Override
    public void onCreate(Bundle savedInstanceState) {
        // PimBridge 参考插件（规格 03 §6 测试壳）：注册后内嵌页可经 Capacitor 代理收发桥消息
        registerPlugin(PimBridgePlugin.class);
        super.onCreate(savedInstanceState);
    }
}
