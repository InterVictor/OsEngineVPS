package net.osa.osenginemobile;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.os.Build;
import android.os.Bundle;
import android.text.Editable;
import android.text.TextWatcher;
import android.util.DisplayMetrics;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowInsets;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import net.schmizz.sshj.SSHClient;
import net.schmizz.sshj.userauth.UserAuthException;

import java.io.IOException;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public final class MainActivity extends Activity {
    static final String EXTRA_RELOGIN = "relogin_message";
    /** the id of the VPS (ProfileStore) the screen is opened for */
    static final String EXTRA_VPS = "vps_id";
    private EditText host;
    private EditText user;
    private EditText password;
    private TextView passwordLabel;
    private CheckBox autoConnect;
    private TextView status;
    private Button connect;
    private ProfileStore profile;
    private EditText vpsName;
    /** the VPS (ProfileStore id) this form connects; the first one unless the screen was opened for another */
    private String vpsId = TerminalKey.FIRST_VPS;
    private final ExecutorService worker = Executors.newSingleThreadExecutor();

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_main);

        ScrollView scrollRoot = findViewById(R.id.scroll_root);
        scrollRoot.setOnApplyWindowInsetsListener((view, insets) -> {
            int top;
            int bottom;
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                android.graphics.Insets bars = insets.getInsets(WindowInsets.Type.systemBars() | WindowInsets.Type.ime());
                top = bars.top;
                bottom = bars.bottom;
            } else {
                top = insets.getSystemWindowInsetTop();
                bottom = insets.getSystemWindowInsetBottom();
            }
            view.setPadding(0, top, 0, bottom);
            return insets;
        });
        scrollRoot.requestApplyInsets();

        vpsName = findViewById(R.id.vps_name);
        host = findViewById(R.id.ssh_host);
        user = findViewById(R.id.ssh_user);
        password = findViewById(R.id.ssh_password);
        passwordLabel = findViewById(R.id.ssh_password_label);
        autoConnect = findViewById(R.id.auto_connect);
        status = findViewById(R.id.connection_status);
        connect = findViewById(R.id.connect_button);
        profile = new ProfileStore(this);
        String requested = getIntent().getStringExtra(EXTRA_VPS);
        if (requested != null && profile.ids().contains(requested)) vpsId = requested;

        vpsName.setText(storedName());
        host.setText(profile.host(vpsId));
        user.setText(profile.user(vpsId));
        autoConnect.setChecked(profile.autoConnect(vpsId));
        TextWatcher targetWatcher = new TextWatcher() {
            @Override public void beforeTextChanged(CharSequence s, int start, int count, int after) { }
            @Override public void onTextChanged(CharSequence s, int start, int before, int count) { }
            @Override public void afterTextChanged(Editable s) { updatePasswordVisibility(); }
        };
        host.addTextChangedListener(targetWatcher);
        user.addTextChangedListener(targetWatcher);
        updatePasswordVisibility();

        autoConnect.setOnCheckedChangeListener((button, checked) -> {
            profile.setAutoConnect(vpsId, checked);
            status.setText(R.string.status_not_connected);
        });
        connect.setOnClickListener(view -> validateForm());

        // Keep the same compact form on a phone and centre it on a wide tablet.
        LinearLayout column = findViewById(R.id.content_column);
        DisplayMetrics metrics = getResources().getDisplayMetrics();
        int maxWidth = Math.round(480 * metrics.density);
        ViewGroup.LayoutParams layout = column.getLayoutParams();
        layout.width = Math.min(metrics.widthPixels - Math.round(48 * metrics.density), maxWidth);
        column.setLayoutParams(layout);

        String savedKey = profile.loadPrivateKey(vpsId, profile.host(vpsId), profile.user(vpsId));
        String lostMessage = getIntent().getStringExtra(EXTRA_RELOGIN);
        if (lostMessage != null) {
            status.setText(lostMessage);
            scheduleRetry();
        }
        else if (profile.autoConnect(vpsId)) {
            if (savedKey != null) beginConnect(profile.host(vpsId), profile.user(vpsId), null, savedKey);
            else {
                // Migrate profiles created by the original password-only Android build.
                String legacyPassword = profile.loadPassword(vpsId, profile.host(vpsId), profile.user(vpsId));
                if (legacyPassword == null) status.setText(R.string.status_auto_needs_password);
                else beginConnect(profile.host(vpsId), profile.user(vpsId), legacyPassword, null);
            }
        } else if (savedKey != null) status.setText(R.string.status_key_ready);

        // the other VPS that are set to auto-connect come up quietly behind the first one (no screen of their own)
        if (lostMessage == null && TerminalKey.FIRST_VPS.equals(vpsId)) connectOthersQuietly();
    }

    private String storedName() {
        String given = profile.name(vpsId);
        // a name that is only the address (the user gave none) is not put in the field
        return given.equals(profile.host(vpsId)) || given.equals("VPS " + vpsId) ? "" : given;
    }

    /** Auto-connect VPS other than this form's: with the device key, on a worker, without any screen; failures are shown on the terminals screen. */
    private void connectOthersQuietly() {
        for (String other : profile.ids()) {
            if (other.equals(vpsId) || !profile.autoConnect(other) || profile.host(other).isEmpty()) continue;
            String otherHost = profile.host(other), otherUser = profile.user(other);
            String key = profile.loadPrivateKey(other, otherHost, otherUser);
            if (key == null || RemoteSsh.isConnected(other)) continue;
            worker.execute(() -> {
                try {
                    SSHClient ssh = RemoteSsh.connectWithKey(this, profile, otherHost, otherUser, key);
                    RemoteSsh.replace(other, ssh, otherHost);
                } catch (Exception ignored) { /* the watch of the app tries again; the terminals screen shows the state */ }
            });
        }
    }

    private final android.os.Handler retryHandler = new android.os.Handler(android.os.Looper.getMainLooper());
    private boolean retrying;

    /** After a lost link keep trying with the device key while this screen is open (the network may be back). */
    private void scheduleRetry() {
        String savedKey = profile.loadPrivateKey(vpsId, profile.host(vpsId), profile.user(vpsId));
        if (savedKey == null || retrying) return;
        retrying = true;
        retryHandler.postDelayed(new Runnable() {
            @Override public void run() {
                retrying = false;
                if (isFinishing() || isDestroyed() || RemoteSsh.isConnected(vpsId)) return;
                if (connect.isEnabled()) beginConnect(profile.host(vpsId), profile.user(vpsId), null, savedKey);
                scheduleRetry();
            }
        }, 8_000);
    }

    @Override protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        String requested = intent.getStringExtra(EXTRA_VPS);
        if (requested != null && profile.ids().contains(requested) && !requested.equals(vpsId)) {
            // the screen was opened again for another VPS: show that VPS
            profile.saveForm(vpsId, host.getText().toString().trim(), user.getText().toString().trim(), autoConnect.isChecked());
            vpsId = requested;
            vpsName.setText(storedName());
            host.setText(profile.host(vpsId));
            user.setText(profile.user(vpsId));
            autoConnect.setChecked(profile.autoConnect(vpsId));
        }
        String lost = intent.getStringExtra(EXTRA_RELOGIN);
        if (lost != null) {
            status.setText(lost);
            scheduleRetry();
        }
        connect.setEnabled(true);
        autoConnect.setEnabled(true);
        updatePasswordVisibility();
    }

    private void updatePasswordVisibility() {
        boolean needsPassword = !profile.hasPrivateKey(vpsId, host.getText().toString().trim(),
            user.getText().toString().trim());
        passwordLabel.setVisibility(needsPassword ? View.VISIBLE : View.GONE);
        password.setVisibility(needsPassword ? View.VISIBLE : View.GONE);
        if (!needsPassword && password.length() > 0) password.setText("");
    }

    @Override
    protected void onPause() {
        super.onPause();
        profile.saveForm(vpsId, host.getText().toString().trim(),
            user.getText().toString().trim(), autoConnect.isChecked());
        saveName();
    }

    @Override
    protected void onDestroy() {
        worker.shutdownNow();
        retryHandler.removeCallbacksAndMessages(null);
        super.onDestroy();
    }

    private void saveName() {
        String name = vpsName.getText().toString().trim();
        if (!name.isEmpty()) profile.rename(vpsId, name);
    }

    private void validateForm() {
        if (host.getText().toString().trim().isEmpty()) {
            host.setError(getString(R.string.error_host));
            host.requestFocus();
            return;
        }
        if (user.getText().toString().trim().isEmpty()) {
            user.setError(getString(R.string.error_user));
            user.requestFocus();
            return;
        }
        String targetHost = host.getText().toString().trim();
        String targetUser = user.getText().toString().trim();
        String savedKey = profile.loadPrivateKey(vpsId, targetHost, targetUser);
        if (password.length() == 0 && savedKey == null) {
            password.setError(getString(R.string.error_password));
            password.requestFocus();
            return;
        }
        profile.saveForm(vpsId, targetHost, targetUser, autoConnect.isChecked());
        saveName();
        beginConnect(targetHost, targetUser, password.getText().toString(), savedKey);
    }

    private void beginConnect(String targetHost, String targetUser,
                              String passwordSecret, String savedKey) {
        connect.setEnabled(false);
        autoConnect.setEnabled(false);
        status.setText(R.string.status_connecting);
        worker.execute(() -> {
            try {
                SSHClient ssh = null;
                String warning = null;
                if (savedKey != null) {
                    try {
                        ssh = RemoteSsh.connectWithKey(this, profile, targetHost, targetUser,
                            savedKey);
                    } catch (UserAuthException revoked) {
                        profile.clearPrivateKey(vpsId);
                        RemoteSsh.close(vpsId);
                        if (passwordSecret == null || passwordSecret.isEmpty())
                            throw new IOException(getString(R.string.status_key_revoked), revoked);
                    }
                }
                if (ssh == null) {
                    if (passwordSecret == null || passwordSecret.isEmpty())
                        throw new IOException(getString(R.string.error_password));
                    ssh = RemoteSsh.connect(this, profile, targetHost, targetUser, passwordSecret);
                    RemoteSsh.replace(vpsId, ssh, targetHost);
                    try {
                        RemoteSsh.registerDeviceKey(this, profile, vpsId, targetHost, targetUser);
                    } catch (Exception registrationError) {
                        warning = getString(R.string.status_key_registration_failed,
                            registrationError.getMessage());
                    } finally {
                        // A failed registration means the next login needs a password again.
                        profile.clearPassword(vpsId);
                    }
                } else {
                    RemoteSsh.replace(vpsId, ssh, targetHost);
                    profile.clearPassword(vpsId);
                }
                String finalWarning = warning;
                runOnUiThread(() -> {
                    if (isFinishing() || isDestroyed()) return;
                    password.setText("");
                    connect.setEnabled(true);
                    autoConnect.setEnabled(true);
                    autoConnect.setChecked(profile.autoConnect(vpsId));
                    updatePasswordVisibility();
                    if (finalWarning == null)
                        startActivity(new Intent(this, TerminalsActivity.class));
                    else new AlertDialog.Builder(this)
                        .setTitle(R.string.key_registration_title)
                        .setMessage(finalWarning)
                        .setPositiveButton(R.string.continue_to_terminals,
                            (dialog, which) -> startActivity(
                                new Intent(this, TerminalsActivity.class)))
                        .show();
                });
            } catch (Exception e) {
                String message = e.getMessage() == null ? getString(R.string.status_connection_failed)
                    : e.getMessage();
                runOnUiThread(() -> {
                    if (isFinishing() || isDestroyed()) return;
                    connect.setEnabled(true);
                    autoConnect.setEnabled(true);
                    updatePasswordVisibility();
                    status.setText(message);
                });
            }
        });
    }
}
