package net.osa.osenginemobile;

import android.app.Activity;
import android.app.AlertDialog;
import android.app.KeyguardManager;
import android.content.Intent;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageInfo;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.View;
import android.widget.CheckBox;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.IOException;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * Settings: the update of the VPS terminals from the newest signed GitHub release (the VPS does the work through
 * "osengine-release", this screen only starts it and shows the progress), the connection and the app version.
 */
public final class SettingsActivity extends Activity {
    private static final int REQUEST_CREDENTIAL = 41;
    private static final String CHECK_COMMAND = "osengine-release check";

    private final Handler handler = new Handler(Looper.getMainLooper());
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final Runnable poll = this::pollStatus;
    private TextView installedView;
    private TextView latestView;
    private TextView stateView;
    private TextView logView;
    private TextView checkButton;
    private TextView applyButton;
    private ProgressBar progress;
    private ProfileStore profile;
    /** the VPS the update section works with (a signed release is installed per VPS) */
    private String updateVps = TerminalKey.FIRST_VPS;
    private LinearLayout vpsList;
    private McpBridge bridge;
    private ServerRelease release;
    private boolean visible;
    private boolean busy;
    private boolean updating;
    private boolean preview;
    private int lostPolls;
    private int idlePolls;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_settings);
        ScreenLayout.apply(this, 560);
        installedView = findViewById(R.id.update_installed);
        latestView = findViewById(R.id.update_latest);
        stateView = findViewById(R.id.update_state);
        logView = findViewById(R.id.update_log);
        checkButton = findViewById(R.id.update_check);
        applyButton = findViewById(R.id.update_apply);
        progress = findViewById(R.id.update_progress);
        profile = new ProfileStore(this);
        findViewById(R.id.back_terminals).setOnClickListener(view -> finish());
        checkButton.setOnClickListener(view -> check());
        applyButton.setOnClickListener(view -> confirmApply());
        setApplyEnabled(false);

        vpsList = findViewById(R.id.vps_list);
        findViewById(R.id.vps_add).setOnClickListener(view -> askNewVps());
        // the update works with the first connected VPS until the user picks another
        for (String id : profile.ids()) if (RemoteSsh.isConnected(id)) { updateVps = id; break; }
        TextView pick = findViewById(R.id.update_vps);
        pick.setOnClickListener(view -> pickUpdateVps());
        renderVpsList();
        ((TextView) findViewById(R.id.about_info)).setText(aboutText());
        android.widget.RadioGroup profitGroup = findViewById(R.id.day_profit_group);
        String profitMode = DayProfit.mode(this);
        profitGroup.check(DayProfit.PER_CONTRACT.equals(profitMode) ? R.id.day_profit_contract
            : DayProfit.DEPOSIT.equals(profitMode) ? R.id.day_profit_deposit : R.id.day_profit_abs);
        profitGroup.setOnCheckedChangeListener((group, id) -> DayProfit.setMode(this,
            id == R.id.day_profit_contract ? DayProfit.PER_CONTRACT
            : id == R.id.day_profit_deposit ? DayProfit.DEPOSIT : DayProfit.ABSOLUTE));

        String previewMode = getIntent().getStringExtra("preview_update");
        preview = (getApplicationInfo().flags & ApplicationInfo.FLAG_DEBUGGABLE) != 0 && previewMode != null;
        if (preview) showPreview(previewMode);
    }

    @Override
    protected void onResume() {
        super.onResume();
        renderVpsList();
        visible = true;
        if (!preview && !updating && !busy) startWatching();
    }

    @Override
    protected void onPause() {
        visible = false;
        handler.removeCallbacks(poll);
        super.onPause();
    }

    @Override
    protected void onDestroy() {
        worker.shutdownNow();
        super.onDestroy();
    }

    // ---- the VPS of this phone ----

    private void renderVpsList() {
        vpsList.removeAllViews();
        java.util.List<String> ids = profile.ids();
        for (int i = 0; i < ids.size(); i++) {
            String id = ids.get(i);
            LinearLayout row = new LinearLayout(this);
            row.setOrientation(LinearLayout.VERTICAL);
            LinearLayout.LayoutParams rowParams = new LinearLayout.LayoutParams(-1, -2);
            if (i > 0) rowParams.topMargin = dp(16);
            vpsList.addView(row, rowParams);

            boolean connected = RemoteSsh.isConnected(id);
            TextView name = new TextView(this);
            name.setText(profile.name(id));
            name.setTextSize(17);
            name.setTypeface(null, android.graphics.Typeface.BOLD);
            name.setTextColor(getColor(R.color.text_primary));
            row.addView(name);
            TextView state = new TextView(this);
            String host = profile.host(id);
            state.setText((connected ? getString(R.string.vps_state_connected) : getString(R.string.vps_state_not_connected))
                + (host.isEmpty() ? "" : " · " + host));
            state.setTextSize(13);
            state.setTextColor(getColor(connected ? R.color.connected : R.color.text_secondary));
            row.addView(state);

            CheckBox auto = new CheckBox(this);
            auto.setText(R.string.vps_auto_row);
            auto.setTextColor(getColor(R.color.text_primary));
            auto.setTextSize(14);
            auto.setChecked(profile.autoConnect(id));
            auto.setOnCheckedChangeListener((button, checked) -> profile.setAutoConnect(id, checked));
            row.addView(auto);

            LinearLayout buttons = new LinearLayout(this);
            buttons.setOrientation(LinearLayout.HORIZONTAL);
            row.addView(buttons, new LinearLayout.LayoutParams(-1, -2));
            buttons.addView(smallButton(getString(R.string.vps_connect_row), true, view ->
                startActivity(new Intent(this, MainActivity.class).putExtra(MainActivity.EXTRA_VPS, id))));
            buttons.addView(smallButton(getString(R.string.vps_rename_button), false, view -> askRename(id)));
            if (!TerminalKey.FIRST_VPS.equals(id))
                buttons.addView(smallButton(getString(R.string.vps_remove_button), false, view -> askRemove(id)));
        }
        refreshUpdateVps();
    }

    private TextView smallButton(String text, boolean accent, android.view.View.OnClickListener listener) {
        TextView button = new TextView(this);
        button.setText(text);
        button.setTextSize(13);
        button.setGravity(android.view.Gravity.CENTER);
        button.setTextColor(getColor(accent ? R.color.orange : R.color.text_primary));
        button.setBackgroundResource(R.drawable.restart_outline);
        button.setPadding(dp(14), 0, dp(14), 0);
        button.setOnClickListener(listener);
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-2, dp(40));
        params.topMargin = dp(6);
        params.rightMargin = dp(8);
        button.setLayoutParams(params);
        return button;
    }

    private void askNewVps() {
        android.widget.EditText input = new android.widget.EditText(this);
        input.setHint(R.string.vps_name_hint);
        input.setSingleLine(true);
        input.setFilters(new android.text.InputFilter[] { new android.text.InputFilter.LengthFilter(30) });
        new android.app.AlertDialog.Builder(this)
            .setTitle(R.string.vps_add_title)
            .setView(input)
            .setNegativeButton(R.string.cancel, null)
            .setPositiveButton(android.R.string.ok, (dialog, which) -> {
                String id = profile.add(input.getText().toString());
                renderVpsList();
                // its address and the password for the first login are entered on the connection screen
                startActivity(new Intent(this, MainActivity.class).putExtra(MainActivity.EXTRA_VPS, id));
            })
            .show();
    }

    private void askRename(String id) {
        android.widget.EditText input = new android.widget.EditText(this);
        input.setSingleLine(true);
        input.setText(profile.name(id));
        input.setFilters(new android.text.InputFilter[] { new android.text.InputFilter.LengthFilter(30) });
        new android.app.AlertDialog.Builder(this)
            .setTitle(R.string.vps_rename_title)
            .setView(input)
            .setNegativeButton(R.string.cancel, null)
            .setPositiveButton(android.R.string.ok, (dialog, which) -> {
                String name = input.getText().toString().trim();
                if (!name.isEmpty()) profile.rename(id, name);
                renderVpsList();
            })
            .show();
    }

    private void askRemove(String id) {
        new android.app.AlertDialog.Builder(this)
            .setTitle(R.string.vps_remove_title)
            .setMessage(getString(R.string.vps_remove_message, profile.name(id)))
            .setNegativeButton(R.string.cancel, null)
            .setPositiveButton(R.string.vps_remove_button, (dialog, which) -> {
                RemoteSsh.close(id);
                profile.remove(id);
                if (updateVps.equals(id)) updateVps = TerminalKey.FIRST_VPS;
                renderVpsList();
            })
            .show();
    }

    /** the update section names its VPS, and lets the user pick another one when there are several */
    private void refreshUpdateVps() {
        TextView pick = findViewById(R.id.update_vps);
        if (profile.ids().size() < 2) { pick.setVisibility(View.GONE); return; }
        if (!profile.ids().contains(updateVps)) updateVps = TerminalKey.FIRST_VPS;
        pick.setVisibility(View.VISIBLE);
        pick.setText(getString(R.string.vps_pick_for_update, profile.name(updateVps)));
    }

    private void pickUpdateVps() {
        if (busy || updating) return;
        java.util.List<String> ids = profile.ids();
        String[] names = new String[ids.size()];
        for (int i = 0; i < names.length; i++) names[i] = profile.name(ids.get(i));
        new android.app.AlertDialog.Builder(this)
            .setItems(names, (dialog, which) -> {
                updateVps = ids.get(which);
                refreshUpdateVps();
                release = null;
                setApplyEnabled(false);
                check();
            })
            .show();
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }

    // on entering: continue watching an update that is already running on the VPS, otherwise check the versions
    private void startWatching() {
        if (!RemoteSsh.isConnected(updateVps)) {
            showState(getString(R.string.update_no_ssh), R.color.orange);
            return;
        }
        busy = true;
        showBusy(true);
        worker.execute(() -> {
            boolean running = false;
            try { running = ServerRelease.Progress.parse(RemoteSsh.run(updateVps, "osengine-release status")).running; }
            catch (IOException ignored) { /* the check below reports the problem */ }
            boolean finalRunning = running;
            runOnUiThread(() -> {
                busy = false;
                if (isDestroyed() || !visible) return;
                if (finalRunning) {
                    beginWatching();
                } else {
                    showBusy(false);
                    check();
                }
            });
        });
    }

    private void check() {
        if (busy || updating) return;
        if (!RemoteSsh.isConnected(updateVps)) {
            showState(getString(R.string.update_no_ssh), R.color.orange);
            return;
        }
        busy = true;
        showBusy(true);
        showState(getString(R.string.update_checking), R.color.text_secondary);
        worker.execute(() -> {
            ServerRelease result = null;
            String error = null;
            try { result = ServerRelease.parse(RemoteSsh.run(updateVps, CHECK_COMMAND)); }
            catch (IOException e) { error = e.getMessage(); }
            ServerRelease finalResult = result;
            String finalError = error;
            runOnUiThread(() -> {
                busy = false;
                if (isDestroyed()) return;
                showBusy(false);
                if (finalResult != null) showRelease(finalResult);
                else showFailure(finalError);
            });
        });
    }

    private void showFailure(String error) {
        release = null;
        setApplyEnabled(false);
        boolean missing = error != null && error.contains("not found");
        showState(missing ? getString(R.string.update_tool_missing) : String.valueOf(error), R.color.orange);
    }

    private void showRelease(ServerRelease result) {
        release = result;
        StringBuilder installed = new StringBuilder(getString(R.string.update_installed_title));
        for (ServerRelease.Terminal terminal : result.terminals) {
            installed.append('\n').append(terminal.name).append("  ").append(terminal.version);
            if (terminal.needsUpdate) installed.append("  →");
        }
        installedView.setText(installed);
        if (result.latestVersion != null) {
            String note = result.latestNote.isEmpty() ? "" : "\n" + result.latestNote;
            latestView.setText(getString(R.string.update_latest_title, result.latestVersion,
                ServerRelease.shortDate(result.latestDate)) + note);
            latestView.setVisibility(View.VISIBLE);
        } else {
            latestView.setVisibility(View.GONE);
        }
        if (result.error != null) {
            showState(result.error, R.color.orange);
            setApplyEnabled(false);
        } else if (result.updateAvailable) {
            showState(getString(R.string.update_available), R.color.orange);
            setApplyEnabled(true);
        } else {
            showState(getString(R.string.update_current), R.color.connected);
            setApplyEnabled(false);
        }
    }

    // ---- update

    private void confirmApply() {
        if (release == null || !release.updateAvailable || updating || busy) return;
        busy = true;
        showBusy(true);
        worker.execute(() -> {
            String positions = openPositionsText(release);
            runOnUiThread(() -> {
                busy = false;
                if (isDestroyed()) return;
                showBusy(false);
                String names = String.join(", ", release.terminalsToUpdate());
                new AlertDialog.Builder(this)
                    .setTitle(R.string.update_confirm_title)
                    .setMessage(getString(R.string.update_confirm_message, release.latestVersion, names, positions))
                    .setNegativeButton(R.string.cancel, null)
                    .setPositiveButton(R.string.update_run, (dialog, which) -> confirmCredential())
                    .show();
            });
        });
    }

    /** Open positions of the terminals that will restart, so the decision is made knowing what is at stake. */
    private String openPositionsText(ServerRelease current) {
        int total = 0;
        StringBuilder details = new StringBuilder();
        boolean unknown = false;
        try {
            if (bridge == null) bridge = new McpBridge(this);
            for (ServerRelease.Terminal terminal : current.terminals) {
                if (!terminal.needsUpdate || !"active".equals(terminal.state)) continue;
                try {
                    Map<String, Object> data = bridge.callBatch(terminal.name,
                        McpBridge.call("bot_journal_get_open_positions", null));
                    Object value = data.get("bot_journal_get_open_positions");
                    int count = -1;
                    if (value instanceof JSONObject) count = ((JSONObject) value).optInt("count", -1);
                    else if (value instanceof JSONArray) count = ((JSONArray) value).length();
                    if (count < 0) { unknown = true; continue; }
                    total += count;
                    if (count > 0) {
                        if (details.length() > 0) details.append(", ");
                        details.append(terminal.name).append(' ').append(count);
                    }
                } catch (Exception e) {
                    unknown = true;
                }
            }
        } catch (Exception e) {
            unknown = true;
        }
        if (unknown) return getString(R.string.update_positions_unknown);
        if (total == 0) return getString(R.string.update_positions_none);
        return getString(R.string.update_positions_some, total, details.toString());
    }

    // with a screen lock the phone asks for the PIN / fingerprint first: the update restarts trading terminals
    private void confirmCredential() {
        KeyguardManager keyguard = (KeyguardManager) getSystemService(KEYGUARD_SERVICE);
        if (keyguard != null && keyguard.isDeviceSecure()) {
            Intent intent = keyguard.createConfirmDeviceCredentialIntent(
                getString(R.string.update_credential_title), getString(R.string.update_credential_message));
            if (intent != null) {
                startActivityForResult(intent, REQUEST_CREDENTIAL);
                return;
            }
        }
        startApply();
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode == REQUEST_CREDENTIAL && resultCode == RESULT_OK) startApply();
    }

    private void startApply() {
        if (updating) return;
        updating = true;
        lostPolls = 0;
        idlePolls = 0;
        setApplyEnabled(false);
        showBusy(true);
        showState(getString(R.string.update_running), R.color.orange);
        logView.setText("");
        logView.setVisibility(View.VISIBLE);
        worker.execute(() -> {
            String error = null;
            try {
                String out = RemoteSsh.run(updateVps, "osengine-release apply");
                if (!out.contains("STARTED")) {
                    error = out.trim().startsWith("ERROR ") ? out.trim().substring(6) : out.trim();
                }
            } catch (IOException e) {
                error = e.getMessage();
            }
            String finalError = error;
            runOnUiThread(() -> {
                if (isDestroyed()) return;
                if (finalError != null) {
                    updating = false;
                    showBusy(false);
                    showState(finalError, R.color.orange);
                    setApplyEnabled(release != null && release.updateAvailable);
                } else {
                    handler.postDelayed(poll, 1500);
                }
            });
        });
    }

    private void beginWatching() {
        updating = true;
        lostPolls = 0;
        idlePolls = 0;
        setApplyEnabled(false);
        showBusy(true);
        showState(getString(R.string.update_running), R.color.orange);
        logView.setVisibility(View.VISIBLE);
        handler.post(poll);
    }

    private void pollStatus() {
        handler.removeCallbacks(poll);
        if (!updating || isDestroyed()) return;
        worker.execute(() -> {
            ServerRelease.Progress state = null;
            try { state = ServerRelease.Progress.parse(RemoteSsh.run(updateVps, "osengine-release status")); }
            catch (IOException ignored) { /* the phone may lose the link; the update goes on at the VPS */ }
            ServerRelease.Progress finalState = state;
            runOnUiThread(() -> {
                if (isDestroyed() || !updating) return;
                if (finalState == null) {
                    if (++lostPolls > 20) {
                        updating = false;
                        showBusy(false);
                        showState(getString(R.string.update_link_lost), R.color.orange);
                    } else if (visible) {
                        handler.postDelayed(poll, 4000);
                    }
                    return;
                }
                lostPolls = 0;
                logView.setText(String.join("\n", finalState.lines));
                if (finalState.finished) {
                    finishUpdate(finalState.ok);
                } else if (!finalState.running && ++idlePolls > 5) {
                    // the VPS unit ended without a DONE line (it was killed): do not wait for ever
                    finishUpdate(false);
                } else if (visible) {
                    handler.postDelayed(poll, 2000);
                }
            });
        });
    }

    private void finishUpdate(boolean ok) {
        updating = false;
        showBusy(false);
        showState(getString(ok ? R.string.update_done : R.string.update_failed), ok ? R.color.connected : R.color.orange);
        handler.postDelayed(this::check, 1500);
    }

    // ---- view helpers

    private void showState(String text, int color) {
        stateView.setText(text);
        stateView.setTextColor(getColor(color));
    }

    private void showBusy(boolean on) {
        progress.setVisibility(on ? View.VISIBLE : View.GONE);
        checkButton.setEnabled(!on && !updating);
        checkButton.setAlpha(on || updating ? 0.45f : 1f);
    }

    private void setApplyEnabled(boolean enabled) {
        applyButton.setEnabled(enabled);
        applyButton.setAlpha(enabled ? 1f : 0.45f);
    }

    private String aboutText() {
        String version = "—";
        try {
            PackageInfo info = getPackageManager().getPackageInfo(getPackageName(), 0);
            version = info.versionName + " (" + info.getLongVersionCode() + ")";
        } catch (Exception ignored) { /* shown as a dash */ }
        return "OsEngine Mobile " + version + "\n" + String.format(Locale.US, "Android %s", android.os.Build.VERSION.RELEASE);
    }

    // ---- debug-build preview of every state without a server: adb ... --es preview_update available|current|error|running|done|failed
    private void showPreview(String mode) {
        String check = "INSTALLED main 5842d961 active\nINSTALLED binance 5842d961 active\n"
            + "LATEST d8840047 2026-10-02T13:09:22Z Серверная сборка d8840047 из osengine-vps, 02.10.2026\n"
            + "STATE main update\nSTATE binance update\nRESULT update-available\n";
        switch (mode) {
            case "current":
                showRelease(ServerRelease.parse(check.replace("5842d961", "d8840047").replace("update", "current")
                    .replace("RESULT current-available", "RESULT up-to-date")));
                break;
            case "error":
                showRelease(ServerRelease.parse("INSTALLED main 5842d961 active\nERROR no signed server release found in InterVictor/OsEngineVPS (or GitHub cannot be reached)\n"));
                break;
            default:
                showRelease(ServerRelease.parse(check));
        }
        if (mode.equals("running") || mode.equals("done") || mode.equals("failed")) {
            String log = "STEP 1/3 looking for the newest release\nOK server-d8840047 (note)\n"
                + "STEP 2/3 downloading and verifying the package\nOK signature is valid, build d8840047\n"
                + "STEP 3/3 updating the terminals one by one\nSTEP [main] osengine\n"
                + "[main] STEP 3/4 switching osengine to the new build\n"
                + "[main] OK MCP API answers on 127.0.0.1:6500\n[main] DONE\nSTEP [binance] osengine-binance\n";
            if (mode.equals("done")) log += "[binance] OK MCP API answers on 127.0.0.1:6501\n[binance] DONE\nDONE ok\n";
            if (mode.equals("failed")) log += "[binance] WARN the new build does not answer — rolling back\n[binance] FAIL update rolled back: osengine-binance runs the previous build again\nFAIL [binance] the update failed (the terminal was rolled back); the other terminals were not touched\nDONE fail\n";
            ServerRelease.Progress state = ServerRelease.Progress.parse((mode.equals("running") ? "RUNNING\n" : "IDLE\n") + log);
            logView.setVisibility(View.VISIBLE);
            logView.setText(String.join("\n", state.lines));
            if (mode.equals("running")) {
                showBusy(true);
                showState(getString(R.string.update_running), R.color.orange);
            } else {
                showState(getString(state.ok ? R.string.update_done : R.string.update_failed),
                    state.ok ? R.color.connected : R.color.orange);
            }
        }
    }
}
