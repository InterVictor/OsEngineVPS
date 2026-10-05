package net.osa.osenginemobile;

/**
 * The key of a terminal on the phone: its technical name for the first VPS ("main", "binance", as before several VPS
 * existed, so nothing saved or passed between screens changes), "&lt;VPS id&gt;/&lt;name&gt;" for the others.
 * Every call to a terminal (MCP bridge, event streams, restart) finds its VPS from the key.
 */
final class TerminalKey {
    static final String FIRST_VPS = "1";

    private TerminalKey() { }

    static String of(String vpsId, String terminalName) {
        return FIRST_VPS.equals(vpsId) ? terminalName : vpsId + "/" + terminalName;
    }

    static String vps(String key) {
        int slash = key == null ? -1 : key.indexOf('/');
        return slash < 0 ? FIRST_VPS : key.substring(0, slash);
    }

    static String name(String key) {
        int slash = key == null ? -1 : key.indexOf('/');
        return slash < 0 ? key : key.substring(slash + 1);
    }
}
