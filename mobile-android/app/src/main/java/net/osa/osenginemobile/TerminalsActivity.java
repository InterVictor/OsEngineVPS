package net.osa.osenginemobile;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.content.pm.ApplicationInfo;
import android.content.res.ColorStateList;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.Gravity;
import android.view.View;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.TextView;
import android.widget.Toast;

import java.io.IOException;
import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.HashSet;
import java.util.Locale;
import java.util.Set;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public final class TerminalsActivity extends Activity {
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    /** one reader per VPS (the CPU load is the difference between two readings of the same VPS) */
    private final java.util.Map<String, VpsSnapshotReader> readers = new java.util.HashMap<>();
    private ProfileStore profile;
    private final Runnable refresh = this::loadSnapshot;
    private TextView status;
    private LinearLayout sections;
    private boolean loading;
    private boolean visible;
    private long vpsRamTotal;
    private boolean preview;
    /** the last snapshot of every VPS (shown dimmed when the VPS cannot be read any more) */
    private final java.util.Map<String, VpsSnapshot> lastSnapshots = new java.util.LinkedHashMap<>();
    private java.util.Map<String, String> problems = new java.util.HashMap<>();
    private final Set<String> restartingServices = new HashSet<>();
    private boolean unavailable;
    /** the open positions and the profit of the day are keyed by the terminal key, the services being restarted by "vps:service" */
    /** Per terminal: open positions and the profit of today (absent until the first answer). */
    private static final class Stats { int open; double profit; }
    private final java.util.Map<String, Stats> stats = new java.util.HashMap<>();
    private McpBridge bridge;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_terminals);
        ScreenLayout.apply(this, 760);
        status = findViewById(R.id.server_status);
        sections = findViewById(R.id.sections_container);
        profile = new ProfileStore(this);
        findViewById(R.id.settings_button).setOnClickListener(view ->
            startActivity(new Intent(this, SettingsActivity.class)));
        preview =(getApplicationInfo().flags & ApplicationInfo.FLAG_DEBUGGABLE) != 0
            && getIntent().getBooleanExtra("preview_terminals", false);
        if (preview) {
            lastSnapshots.put(TerminalKey.FIRST_VPS, previewSnapshot());
            renderAll();
            status.setText(R.string.terminal_preview);
            return;
        }
        status.setText(RemoteSsh.host() == null ? getString(R.string.status_not_connected)
            : RemoteSsh.host() + " · SSH");
    }

    @Override
    protected void onResume() {
        super.onResume();
        visible = true;
        if (!preview) handler.post(refresh);
    }

    @Override
    protected void onPause() {
        visible = false;
        handler.removeCallbacks(refresh);
        super.onPause();
    }

    @Override
    protected void onDestroy() {
        worker.shutdownNow();
        super.onDestroy();
    }

    /** the VPS shown on the screen: those with an address (or already connected); the first one when nothing is set up yet */
    private java.util.List<String> configuredVps() {
        java.util.List<String> result = new java.util.ArrayList<>();
        for (String id : profile.ids())
            if (!profile.host(id).isEmpty() || RemoteSsh.isConnected(id)) result.add(id);
        if (result.isEmpty()) result.add(TerminalKey.FIRST_VPS);
        return result;
    }

    private void loadSnapshot() {
        handler.removeCallbacks(refresh);
        if (!visible || loading) return;
        java.util.List<String> vps = configuredVps();
        if (!RemoteSsh.isConnected()) {
            // nothing connected: the sections show what was seen last and a button to connect each VPS
            if (!lastSnapshots.isEmpty() || vps.size() > 1) renderAll();
            status.setText(R.string.status_not_connected);
            return;
        }
        loading = true;
        worker.execute(() -> {
            java.util.Map<String, VpsSnapshot> fresh = new java.util.LinkedHashMap<>();
            java.util.Map<String, String> failed = new java.util.HashMap<>();
            for (String id : vps) {
                if (!RemoteSsh.isConnected(id)) continue;
                VpsSnapshotReader reader = readers.get(id);
                if (reader == null) { reader = new VpsSnapshotReader(id); readers.put(id, reader); }
                try {
                    VpsSnapshot snapshot = reader.read();
                    loadStats(snapshot);
                    fresh.put(id, snapshot);
                } catch (Exception e) {
                    failed.put(id, e.getMessage() == null ? "ошибка" : e.getMessage());
                }
            }
            runOnUiThread(() -> {
                loading = false;
                if (!visible || isDestroyed()) return;
                lastSnapshots.putAll(fresh);
                problems = failed;
                renderAll();
                handler.postDelayed(refresh, 10_000);
            });
        });
    }

    /** Open positions (sum over the robots) and today profit of every active terminal; failures keep old values. */
    private void loadStats(VpsSnapshot snapshot) {
        try { if (bridge == null) bridge = new McpBridge(this); }
        catch (Exception e) { return; }
        String mode = DayProfit.mode(this);
        for (VpsSnapshot.Terminal terminal : snapshot.terminals) {
            if (!"active".equals(terminal.state)) continue;
            try {
                java.util.Map<String, Object> result = bridge.callBatch(terminal.key(),
                    McpBridge.call("bot_get_list", null),
                    McpBridge.call("bot_journal_get_equity", new org.json.JSONObject().put("chart_type", mode)));
                Object list = result.get("bot_get_list");
                Object equity = result.get("bot_journal_get_equity");
                if (!(list instanceof org.json.JSONObject) || !(equity instanceof org.json.JSONObject)) continue;
                org.json.JSONArray bots = ((org.json.JSONObject) list).optJSONArray("bots");
                org.json.JSONArray points = ((org.json.JSONObject) equity).optJSONArray("points");
                if (bots == null) continue;
                Stats value = new Stats();
                for (int i = 0; i < bots.length(); i++) {
                    org.json.JSONObject bot = bots.optJSONObject(i);
                    if (bot != null) value.open += bot.optInt("open_positions_count");
                }
                value.profit = points == null ? 0 : DayProfit.today(points);
                synchronized (stats) { stats.put(terminal.key(), value); }
            } catch (Exception ignored) { /* keep the previous numbers */ }
        }
    }

    /** One section per VPS: its name (when there are several), its resources and its terminals, or a button to connect it. */
    private void renderAll() {
        java.util.List<String> vps = configuredVps();
        boolean several = vps.size() > 1;
        sections.removeAllViews();
        StringBuilder hosts = new StringBuilder();
        String firstProblem = null;
        for (String id : vps) {
            View section = getLayoutInflater().inflate(R.layout.item_vps_section, sections, false);
            TextView title = section.findViewById(R.id.vps_title);
            TextView state = section.findViewById(R.id.vps_state);
            TextView connect = section.findViewById(R.id.vps_connect);
            View body = section.findViewById(R.id.vps_body);
            boolean connected = RemoteSsh.isConnected(id);
            String problem = problems.get(id);
            VpsSnapshot snapshot = lastSnapshots.get(id);
            if (several) {
                title.setVisibility(View.VISIBLE);
                title.setText(profile.name(id));
                if (!(sections.getChildCount() > 0)) ((LinearLayout.LayoutParams) title.getLayoutParams()).topMargin = dp(18);
            }
            if (connected && RemoteSsh.host(id) != null) {
                if (hosts.length() > 0) hosts.append(", ");
                hosts.append(RemoteSsh.host(id));
            }
            if (problem != null && firstProblem == null) firstProblem = problem;
            if (snapshot == null) {
                // one connected VPS keeps the placeholders of the resource panel until the first reading arrives
                body.setVisibility(connected && !several ? View.VISIBLE : View.GONE);
                if (several || !connected) {
                    state.setVisibility(View.VISIBLE);
                    state.setText(connected ? getString(R.string.terminal_loading)
                        : getString(R.string.vps_not_connected, profile.host(id).isEmpty() ? "—" : profile.host(id)));
                }
                if (!connected) {
                    connect.setVisibility(View.VISIBLE);
                    connect.setOnClickListener(view -> startActivity(new Intent(this, MainActivity.class)
                        .putExtra(MainActivity.EXTRA_VPS, id)));
                }
            } else {
                boolean available = !preview ? connected && problem == null : true;
                if (several && (!connected || problem != null)) {
                    state.setVisibility(View.VISIBLE);
                    state.setText(!connected ? getString(R.string.vps_connection_lost) : problem);
                    if (!connected) {
                        connect.setVisibility(View.VISIBLE);
                        connect.setOnClickListener(view -> startActivity(new Intent(this, MainActivity.class)
                            .putExtra(MainActivity.EXTRA_VPS, id)));
                    }
                }
                fillSection(section, snapshot, available, several);
            }
            sections.addView(section);
        }
        if (!preview) {
            if (firstProblem != null) status.setText(firstProblem);
            else status.setText((hosts.length() == 0 ? getString(R.string.status_not_connected) : hosts + " · SSH")
                + (hosts.length() == 0 ? "" : " · " + new SimpleDateFormat("HH:mm:ss", Locale.getDefault()).format(new Date())));
        }
    }

    private void fillSection(View section, VpsSnapshot snapshot, boolean available, boolean several) {
        unavailable = !available;
        vpsRamTotal = snapshot.ramTotal;
        ((TextView) section.findViewById(R.id.cpu_value)).setText("CPU  " + percent(snapshot.cpuPercent));
        ((TextView) section.findViewById(R.id.ram_value)).setText("RAM  " + percent(snapshot.ramPercent));
        ((TextView) section.findViewById(R.id.disk_value)).setText("Диск  " + percent(snapshot.diskPercent));
        ((ProgressBar) section.findViewById(R.id.cpu_bar)).setProgress(
            Double.isNaN(snapshot.cpuPercent) ? 0 : (int) Math.round(snapshot.cpuPercent));
        ((ProgressBar) section.findViewById(R.id.ram_bar)).setProgress(
            Double.isNaN(snapshot.ramPercent) ? 0 : (int) Math.round(snapshot.ramPercent));
        ((ProgressBar) section.findViewById(R.id.disk_bar)).setProgress(
            Double.isNaN(snapshot.diskPercent) ? 0 : (int) Math.round(snapshot.diskPercent));
        ((TextView) section.findViewById(R.id.cpu_total)).setText(snapshot.cores > 0 ? coresText(snapshot.cores) : "");
        ((TextView) section.findViewById(R.id.ram_total)).setText(snapshot.ramTotal > 0 ? gigabytes(snapshot.ramTotal) : "");
        ((TextView) section.findViewById(R.id.disk_total)).setText(snapshot.diskTotal > 0 ? gigabytes(snapshot.diskTotal) : "");
        LinearLayout terminalList = section.findViewById(R.id.terminal_list);
        terminalList.removeAllViews();
        if (snapshot.terminals.isEmpty()) {
            TextView empty = label(getString(R.string.terminal_empty), 14, R.color.text_secondary);
            terminalList.addView(empty);
            return;
        }
        for (VpsSnapshot.Terminal terminal : snapshot.terminals) {
            if (available && RemoteSsh.isConnected(terminal.vpsId)) AlertCenter.ensureStream(this, terminal.key());
            terminalList.addView(card(terminal));
        }
    }

    /** journalTab: 0 «Эквити» (chart), 1 «Открытые позиции»; the zone opens that journal tab of the terminal. */
    private View statZone(String big, String caption, int color, int journalTab,
                          VpsSnapshot.Terminal terminal) {
        LinearLayout zone = new LinearLayout(this);
        zone.setOrientation(LinearLayout.VERTICAL);
        zone.setGravity(Gravity.CENTER);
        zone.setBackgroundResource(R.drawable.input_background);
        zone.setOnClickListener(view -> openRobots(terminal, "Журнал", journalTab));
        zone.setContentDescription((journalTab == 1 ? "Открытые позиции " : "Профит за день ") + terminal.shownName());
        TextView number = label(big, big.length() > 7 ? 18 : 26, color);
        number.setTypeface(null, android.graphics.Typeface.BOLD);
        number.setGravity(Gravity.CENTER);
        number.setSingleLine(true);
        zone.addView(number, new LinearLayout.LayoutParams(-2, -2));
        TextView text = label(caption, 11, R.color.text_secondary);
        text.setGravity(Gravity.CENTER);
        zone.addView(text, new LinearLayout.LayoutParams(-2, -2));
        return zone;
    }

    private View card(VpsSnapshot.Terminal terminal) {
        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setPadding(dp(16), dp(12), dp(16), dp(12));
        card.setBackgroundResource(R.drawable.input_background);
        LinearLayout.LayoutParams cardParams = new LinearLayout.LayoutParams(-1, -2);
        cardParams.bottomMargin = dp(10);
        card.setLayoutParams(cardParams);
        TextView name = label(terminal.shownName(), 20, R.color.text_primary);
        name.setTypeface(null, android.graphics.Typeface.BOLD);
        card.addView(name, new LinearLayout.LayoutParams(-1, -2));
        // three zones under the name: open positions | profit of the day | open / restart
        LinearLayout header = new LinearLayout(this);
        header.setOrientation(LinearLayout.HORIZONTAL);
        header.setGravity(Gravity.CENTER_VERTICAL);
        LinearLayout.LayoutParams headerParams = new LinearLayout.LayoutParams(-1, -2);
        headerParams.topMargin = dp(6);
        card.addView(header, headerParams);
        Stats value;
        synchronized (stats) { value = stats.get(terminal.key()); }
        String mode = DayProfit.mode(this);
        header.addView(statZone(value == null ? "—" : String.valueOf(value.open),
            "открытых позиций", R.color.text_primary, 1, terminal),
            new LinearLayout.LayoutParams(0, dp(78), 1));
        int profitColor = value == null || Math.abs(value.profit) < 0.005 ? R.color.text_primary
            : value.profit > 0 ? R.color.connected : R.color.loss;
        header.addView(statZone(value == null ? "—" : DayProfit.format(value.profit, mode),
            "за день · " + DayProfit.label(mode).toLowerCase(Locale.ROOT), profitColor, 0, terminal),
            new LinearLayout.LayoutParams(0, dp(78), 1.25f));
        LinearLayout actions = new LinearLayout(this);
        actions.setOrientation(LinearLayout.VERTICAL);
        LinearLayout.LayoutParams actionsParams = new LinearLayout.LayoutParams(dp(96), -2);
        actionsParams.leftMargin = dp(8);
        header.addView(actions, actionsParams);
        TextView open = label(getString(R.string.terminal_open_button), 14, R.color.text_primary);
        open.setGravity(Gravity.CENTER);
        open.setBackgroundResource(R.drawable.button_background);
        open.setContentDescription(getString(R.string.terminal_open_robots, terminal.shownName()));
        actions.addView(open, new LinearLayout.LayoutParams(-1, dp(36)));
        open.setOnClickListener(view -> openRobots(terminal));
        open.setEnabled(!preview);
        if (preview) open.setAlpha(0.45f);
        TextView restart = label(getString(R.string.terminal_restart_button), 14, R.color.orange);
        restart.setGravity(Gravity.CENTER);
        restart.setBackgroundResource(R.drawable.restart_outline);
        restart.setContentDescription(getString(R.string.terminal_restart_accessibility,
            terminal.shownName()));
        restart.setTooltipText(getString(R.string.terminal_restart_accessibility,
            terminal.shownName()));
        LinearLayout.LayoutParams restartParams = new LinearLayout.LayoutParams(-1, dp(36));
        restartParams.topMargin = dp(5);
        actions.addView(restart, restartParams);
        restart.setOnClickListener(view -> confirmRestart(terminal));
        boolean canRestart = !preview && !unavailable
            && !restartingServices.contains(terminal.vpsId + ":" + terminal.service);
        restart.setEnabled(canRestart);
        if (!canRestart) restart.setAlpha(0.45f);

        String state;
        int stateColor = R.color.text_secondary;
        if (unavailable) {
            state = getString(R.string.terminal_state_inactive);
        } else if (restartingServices.contains(terminal.vpsId + ":" + terminal.service)) {
            state = getString(R.string.terminal_state_restarting);
            stateColor = R.color.orange;
        } else if ("active".equals(terminal.state)) {
            state = getString(R.string.terminal_state_active);
            stateColor = R.color.connected;
        } else if ("inactive".equals(terminal.state)) {
            state = getString(R.string.terminal_state_inactive);
        } else if ("failed".equals(terminal.state)) {
            state = getString(R.string.terminal_state_failed);
            stateColor = R.color.orange;
        } else if ("activating".equals(terminal.state)) {
            state = getString(R.string.terminal_state_starting);
        } else if ("deactivating".equals(terminal.state)) {
            state = getString(R.string.terminal_state_stopping);
        } else if ("reloading".equals(terminal.state)) {
            state = getString(R.string.terminal_state_reloading);
        } else {
            state = getString(R.string.terminal_state_other, terminal.state);
        }
        TextView stateLabel = label(state, 14, stateColor);
        LinearLayout.LayoutParams stateParams = new LinearLayout.LayoutParams(-1, -2);
        stateParams.topMargin = dp(5);
        card.addView(stateLabel, stateParams);
        addMetric(card, "CPU", percent(terminal.cpuPercent), terminal.cpuPercent);
        double ramPercent = vpsRamTotal > 0
            ? 100.0 * terminal.memoryBytes / vpsRamTotal : Double.NaN;
        addMetric(card, "RAM", memory(terminal.memoryBytes) + "  ·  "
            + percent(ramPercent) + " VPS", ramPercent);
        card.setContentDescription(getString(R.string.terminal_open_robots, terminal.shownName()));
        card.setClickable(true);
        card.setFocusable(true);
        card.setOnClickListener(view -> openRobots(terminal));
        return card;
    }

    private void openRobots(VpsSnapshot.Terminal terminal) {
        openRobots(terminal, null, 0);
    }

    private void openRobots(VpsSnapshot.Terminal terminal, String startPage, int journalTab) {
        if (preview) return;
        Intent intent = new Intent(this, RobotsActivity.class);
        intent.putExtra("terminal_name", terminal.key());
        // with several VPS the title tells which VPS the terminal is on
        intent.putExtra("terminal_title", configuredVps().size() > 1
            ? profile.name(terminal.vpsId) + " · " + terminal.shownName() : terminal.shownName());
        if (startPage != null) {
            intent.putExtra("start_page", startPage);
            intent.putExtra("journal_tab", journalTab);
        }
        startActivity(intent);
    }

    private void addMetric(LinearLayout card, String title, String value, double amount) {
        TextView text = label(title + "  " + value, 14, R.color.text_primary);
        LinearLayout.LayoutParams textParams = new LinearLayout.LayoutParams(-1, -2);
        textParams.topMargin = dp(10);
        card.addView(text, textParams);
        ProgressBar bar = new ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal);
        bar.setMax(100);
        bar.setProgress(Double.isNaN(amount) ? 0 : (int) Math.round(amount));
        bar.setProgressTintList(ColorStateList.valueOf(getColor(R.color.orange)));
        LinearLayout.LayoutParams barParams = new LinearLayout.LayoutParams(-1, dp(5));
        barParams.topMargin = dp(5);
        card.addView(bar, barParams);
    }

    private static VpsSnapshot previewSnapshot() {
        VpsSnapshot snapshot = new VpsSnapshot();
        snapshot.cpuPercent = 31.2;
        snapshot.ramPercent = 62.4;
        snapshot.diskPercent = 41.8;
        snapshot.ramTotal = 2L * 1024 * 1024 * 1024;
        VpsSnapshot.Terminal main = new VpsSnapshot.Terminal();
        main.name = "main";
        main.service = "osengine";
        main.state = "active";
        main.cpuPercent = 12.6;
        main.memoryBytes = 608L * 1024 * 1024;
        snapshot.terminals.add(main);
        VpsSnapshot.Terminal binance = new VpsSnapshot.Terminal();
        binance.name = "binance";
        binance.service = "osengine-binance";
        binance.state = "active";
        binance.cpuPercent = 18.4;
        binance.memoryBytes = 384L * 1024 * 1024;
        snapshot.terminals.add(binance);
        return snapshot;
    }

    private void confirmRestart(VpsSnapshot.Terminal terminal) {
        new AlertDialog.Builder(this)
            .setTitle(R.string.restart_confirm_title)
            .setMessage(getString(R.string.restart_confirm_message, terminal.shownName()))
            .setNegativeButton(R.string.cancel, null)
            .setPositiveButton(R.string.restart, (dialog, which) -> restart(terminal.vpsId, terminal.service))
            .show();
    }

    private void restart(String vpsId, String service) {
        if (!service.matches("osengine(?:-[a-z0-9-]+)?")) return;
        String restartKey = vpsId + ":" + service;
        restartingServices.add(restartKey);
        if (!lastSnapshots.isEmpty()) renderAll();
        status.setText(R.string.terminal_restarting);
        worker.execute(() -> {
            String message;
            try {
                RemoteSsh.run(vpsId, "systemctl restart " + service);
                message = getString(R.string.terminal_restart_started);
            } catch (IOException e) {
                message = getString(R.string.terminal_restart_failed, e.getMessage());
            }
            String finalMessage = message;
            runOnUiThread(() -> {
                if (isDestroyed()) return;
                restartingServices.remove(restartKey);
                Toast.makeText(this, finalMessage, Toast.LENGTH_LONG).show();
                if (visible) handler.post(refresh);
            });
        });
    }

    private TextView label(String value, int sp, int color) {
        TextView text = new TextView(this);
        text.setText(value);
        text.setTextSize(sp);
        text.setTextColor(getColor(color));
        return text;
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }

    private static String percent(double value) {
        return Double.isNaN(value) ? "—" : String.format(Locale.US, "%.1f%%", value);
    }

    private static String coresText(int cores) {
        int tail = cores % 100;
        String word = tail >= 11 && tail <= 14 ? "ядер" : cores % 10 == 1 ? "ядро"
            : cores % 10 >= 2 && cores % 10 <= 4 ? "ядра" : "ядер";
        return cores + " " + word;
    }

    private static String gigabytes(long bytes) {
        double gb = bytes / 1_073_741_824.0;
        return gb >= 100 ? String.format(Locale.US, "%.0f ГБ", gb) : String.format(Locale.US, "%.1f ГБ", gb);
    }

    private static String memory(long bytes) {
        return bytes <= 0 ? "—" : String.format(Locale.US, "%.0f MB", bytes / 1_048_576.0);
    }
}
