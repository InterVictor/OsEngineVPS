package net.osa.osenginemobile;

import android.app.Activity;
import android.app.Application;
import android.content.Intent;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;

import net.schmizz.sshj.SSHClient;
import net.schmizz.sshj.userauth.UserAuthException;

import java.lang.ref.WeakReference;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * Opens the common alert window when an alert arrives and keeps the SSH link alive: when it drops while
 * a screen is open, the app silently logs in again with the device key; only when that is impossible
 * (key revoked, no key, repeated failures) the login screen is shown with an explanation.
 */
public final class OsApp extends Application {
    private static final long WATCH_MS = 10_000;
    private static final int FAILURES_BEFORE_LOGIN = 12;   // about two minutes of silent retries

    private static WeakReference<Activity> resumed = new WeakReference<>(null);
    private final Handler main = new Handler(Looper.getMainLooper());
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private boolean reconnecting;
    private final java.util.Map<String, Integer> failures = new java.util.HashMap<>();
    private final Runnable watch = new Runnable() {
        @Override public void run() {
            check();
            main.postDelayed(this, WATCH_MS);
        }
    };

    @Override public void onCreate() {
        super.onCreate();
        registerActivityLifecycleCallbacks(new ActivityLifecycleCallbacks() {
            @Override public void onActivityResumed(Activity activity) {
                resumed = new WeakReference<>(activity);
                main.removeCallbacks(watch);
                main.postDelayed(watch, 1_500);
            }
            @Override public void onActivityPaused(Activity activity) {
                if (resumed.get() == activity) resumed = new WeakReference<>(null);
            }
            @Override public void onActivityCreated(Activity activity, Bundle state) { }
            @Override public void onActivityStarted(Activity activity) { Ime.attach(activity); }
            @Override public void onActivityStopped(Activity activity) { }
            @Override public void onActivitySaveInstanceState(Activity activity, Bundle state) { }
            @Override public void onActivityDestroyed(Activity activity) { }
        });
        AlertCenter.addListener(added -> {
            if (!added) return;
            main.post(() -> {
                Activity activity = resumed.get();
                if (activity == null || activity instanceof AlertsActivity) return;
                activity.startActivity(new Intent(activity, AlertsActivity.class));
            });
        });
    }

    /**
     * Runs on the main thread: nothing to do while every VPS that was connected is still connected, on the login screen or in
     * the background. A lost VPS is logged in again silently; the login screen of that VPS is shown only when no VPS is
     * connected at all and the silent way is impossible (the other VPS keep working meanwhile).
     */
    private void check() {
        Activity activity = resumed.get();
        if (activity == null) { main.removeCallbacks(watch); return; }   // restarted on the next resume
        if (activity instanceof MainActivity) return;
        if (reconnecting) return;
        ProfileStore profile = new ProfileStore(this);
        // the VPS to keep connected: the first one always (as before several VPS existed), the others if they were connected
        // in this run or are set to auto-connect; one that was never set up (no address) is not wanted
        String target = null;
        String keyless = null;
        for (String id : profile.ids()) {
            boolean wanted = TerminalKey.FIRST_VPS.equals(id) || RemoteSsh.wasConnected(id) || profile.autoConnect(id);
            if (!wanted || profile.host(id).isEmpty() || RemoteSsh.isConnected(id)) continue;
            if (profile.loadPrivateKey(id, profile.host(id), profile.user(id)) != null) { target = id; break; }
            if (keyless == null) keyless = id;
        }
        if (target == null) {
            failures.clear();
            if (keyless != null && !RemoteSsh.isConnected())
                showLogin(activity, getString(R.string.status_connection_lost_login), keyless);
            return;
        }
        String vpsId = target;
        String host = profile.host(vpsId), user = profile.user(vpsId);
        String key = profile.loadPrivateKey(vpsId, host, user);
        reconnecting = true;
        worker.execute(() -> {
            String problem = null;
            boolean login = false;
            try {
                SSHClient ssh = RemoteSsh.connectWithKey(activity, profile, host, user, key);
                RemoteSsh.replace(vpsId, ssh, host);
                AlertCenter.restartStreams(getApplicationContext());
            } catch (UserAuthException revoked) {
                profile.clearPrivateKey(vpsId);
                problem = getString(R.string.status_key_revoked);
                login = true;
            } catch (Exception e) {
                problem = e.getMessage();
            }
            String finalProblem = problem;
            boolean forceLogin = login;
            main.post(() -> {
                reconnecting = false;
                if (finalProblem == null) { failures.remove(vpsId); return; }
                int count = failures.containsKey(vpsId) ? failures.get(vpsId) + 1 : 1;
                failures.put(vpsId, count);
                Activity current = resumed.get();
                if (current != null && !(current instanceof MainActivity) && !RemoteSsh.isConnected()
                    && (forceLogin || count >= FAILURES_BEFORE_LOGIN))
                    showLogin(current, forceLogin ? finalProblem
                        : getString(R.string.status_connection_lost_login) + "\n" + finalProblem, vpsId);
            });
        });
    }

    private void showLogin(Activity from, String message, String vpsId) {
        failures.remove(vpsId);
        Intent intent = new Intent(from, MainActivity.class)
            .addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP | Intent.FLAG_ACTIVITY_SINGLE_TOP)
            .putExtra(MainActivity.EXTRA_RELOGIN, message)
            .putExtra(MainActivity.EXTRA_VPS, vpsId);
        from.startActivity(intent);
    }
}
