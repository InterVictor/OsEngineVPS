package net.osa.osenginemobile;

import android.content.Context;
import android.content.SharedPreferences;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;

import java.nio.charset.StandardCharsets;
import java.security.KeyStore;
import java.security.SecureRandom;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;

import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

/**
 * The VPS this phone connects to. There may be several: each has an id, a name given by the user (for example the
 * provider's name), an address, a user, the device key (encrypted with the Android Keystore) and the auto-connect flag.
 * The first VPS (id "1") keeps the preference names of the single-VPS versions, the others get "_<id>" appended,
 * so nothing is migrated. The trusted host keys do not depend on the VPS id (they are kept per address).
 */
final class ProfileStore {
    private static final String STORE = "vps_profile";
    // Keep the alias so existing password-only profiles can be migrated once.
    private static final String KEY_ALIAS = "osengine_mobile_ssh_password";
    private static final String IDS = "vps_ids";

    private final SharedPreferences prefs;

    ProfileStore(Context context) {
        prefs = context.getSharedPreferences(STORE, Context.MODE_PRIVATE);
    }

    // ---- the list of VPS ----

    /** ids of the VPS in the order they were added; "1" is always there */
    List<String> ids() {
        List<String> result = new ArrayList<>();
        for (String id : prefs.getString(IDS, TerminalKey.FIRST_VPS).split(",")) {
            id = id.trim();
            if (!id.isEmpty() && id.matches("[0-9]+") && !result.contains(id)) result.add(id);
        }
        if (!result.contains(TerminalKey.FIRST_VPS)) result.add(0, TerminalKey.FIRST_VPS);
        return result;
    }

    /** the name of a VPS: the one the user gave, else its address, else "VPS <id>" */
    String name(String id) {
        String name = prefs.getString("vps_name" + suffix(id), "").trim();
        if (!name.isEmpty()) return name;
        String host = host(id);
        return host.isEmpty() ? "VPS " + id : host;
    }

    void rename(String id, String name) {
        prefs.edit().putString("vps_name" + suffix(id), name.trim()).apply();
    }

    /** makes a new, empty VPS and returns its id */
    String add(String name) {
        int next = 1;
        for (String id : ids()) next = Math.max(next, Integer.parseInt(id) + 1);
        String id = String.valueOf(next);
        List<String> all = ids();
        all.add(id);
        prefs.edit().putString(IDS, join(all)).putString("vps_name" + suffix(id), name.trim())
            .putString("ssh_user" + suffix(id), "root").commit();
        return id;
    }

    /** forgets a VPS (its address, user, key and name); the first VPS cannot be removed, only emptied */
    void remove(String id) {
        if (TerminalKey.FIRST_VPS.equals(id)) return;
        List<String> all = ids();
        all.remove(id);
        String s = suffix(id);
        prefs.edit().putString(IDS, join(all)).remove("vps_name" + s).remove("ssh_host" + s)
            .remove("ssh_user" + s).remove("auto_connect" + s).remove("ssh_private_key_encrypted" + s)
            .remove("private_key_host" + s).remove("private_key_user" + s).remove("private_key_comment" + s)
            .remove("ssh_password_encrypted" + s).remove("password_host" + s).remove("password_user" + s)
            .commit();
    }

    private static String join(List<String> values) {
        StringBuilder text = new StringBuilder();
        for (String value : values) { if (text.length() > 0) text.append(','); text.append(value); }
        return text.toString();
    }

    private static String suffix(String id) {
        return TerminalKey.FIRST_VPS.equals(id) ? "" : "_" + id;
    }

    // ---- one VPS ----

    String host() { return host(TerminalKey.FIRST_VPS); }
    String host(String id) { return prefs.getString("ssh_host" + suffix(id), ""); }
    String user() { return user(TerminalKey.FIRST_VPS); }
    String user(String id) { return prefs.getString("ssh_user" + suffix(id), "root"); }
    boolean autoConnect() { return autoConnect(TerminalKey.FIRST_VPS); }
    boolean autoConnect(String id) { return prefs.getBoolean("auto_connect" + suffix(id), false); }

    boolean hasPrivateKey(String host, String user) { return hasPrivateKey(TerminalKey.FIRST_VPS, host, user); }
    boolean hasPrivateKey(String id, String host, String user) {
        String s = suffix(id);
        return host.equals(prefs.getString("private_key_host" + s, ""))
            && user.equals(prefs.getString("private_key_user" + s, ""))
            && prefs.contains("ssh_private_key_encrypted" + s);
    }

    void saveForm(String host, String user, boolean auto) { saveForm(TerminalKey.FIRST_VPS, host, user, auto); }
    void saveForm(String id, String host, String user, boolean auto) {
        String s = suffix(id);
        prefs.edit().putString("ssh_host" + s, host).putString("ssh_user" + s, user)
            .putBoolean("auto_connect" + s, auto).apply();
        if (!auto) clearPassword(id);
    }

    void setAutoConnect(boolean auto) { setAutoConnect(TerminalKey.FIRST_VPS, auto); }
    void setAutoConnect(String id, boolean auto) {
        prefs.edit().putBoolean("auto_connect" + suffix(id), auto).apply();
        if (!auto) clearPassword(id);
    }

    String knownHost(String host, int port) {
        return prefs.getString("host_key_" + host + ":" + port, null);
    }

    void trustHost(String host, int port, byte[] encodedKey) {
        prefs.edit().putString("host_key_" + host + ":" + port,
            Base64.encodeToString(encodedKey, Base64.NO_WRAP)).apply();
    }

    boolean matchesKnownHost(String host, int port, byte[] encodedKey) {
        String saved = knownHost(host, port);
        return saved != null && saved.equals(Base64.encodeToString(encodedKey, Base64.NO_WRAP));
    }

    void savePrivateKey(String host, String user, String privateKey, String comment) throws Exception {
        savePrivateKey(TerminalKey.FIRST_VPS, host, user, privateKey, comment);
    }

    void savePrivateKey(String id, String host, String user, String privateKey, String comment) throws Exception {
        String s = suffix(id);
        String encrypted = encrypt(privateKey);
        if (!prefs.edit().putString("ssh_private_key_encrypted" + s, encrypted)
            .putString("private_key_host" + s, host).putString("private_key_user" + s, user)
            .putString("private_key_comment" + s, comment)
            .remove("ssh_password_encrypted" + s).remove("password_host" + s).remove("password_user" + s).commit())
            throw new IllegalStateException("Не удалось сохранить SSH-ключ устройства");
    }

    String loadPrivateKey(String host, String user) { return loadPrivateKey(TerminalKey.FIRST_VPS, host, user); }
    String loadPrivateKey(String id, String host, String user) {
        String s = suffix(id);
        if (!host.equals(prefs.getString("private_key_host" + s, ""))
            || !user.equals(prefs.getString("private_key_user" + s, ""))) return null;
        String stored = prefs.getString("ssh_private_key_encrypted" + s, null);
        if (stored == null) return null;
        try { return decrypt(stored); }
        catch (Exception e) {
            clearPrivateKey(id);
            return null;
        }
    }

    void clearPrivateKey() { clearPrivateKey(TerminalKey.FIRST_VPS); }
    void clearPrivateKey(String id) {
        String s = suffix(id);
        prefs.edit().remove("ssh_private_key_encrypted" + s).remove("private_key_host" + s)
            .remove("private_key_user" + s).remove("private_key_comment" + s).commit();
    }

    private String encrypt(String value) throws Exception {
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
        cipher.init(Cipher.ENCRYPT_MODE, key());
        byte[] encrypted = cipher.doFinal(value.getBytes(StandardCharsets.UTF_8));
        byte[] payload = new byte[cipher.getIV().length + encrypted.length];
        System.arraycopy(cipher.getIV(), 0, payload, 0, cipher.getIV().length);
        System.arraycopy(encrypted, 0, payload, cipher.getIV().length, encrypted.length);
        String result = Base64.encodeToString(payload, Base64.NO_WRAP);
        Arrays.fill(payload, (byte) 0);
        return result;
    }

    String loadPassword(String host, String user) { return loadPassword(TerminalKey.FIRST_VPS, host, user); }
    String loadPassword(String id, String host, String user) {
        String s = suffix(id);
        if (!host.equals(prefs.getString("password_host" + s, ""))
            || !user.equals(prefs.getString("password_user" + s, ""))) return null;
        String stored = prefs.getString("ssh_password_encrypted" + s, null);
        if (stored == null) return null;
        try { return decrypt(stored); }
        catch (Exception e) {
            clearPassword(id);
            return null;
        }
    }

    private String decrypt(String stored) throws Exception {
        byte[] payload = Base64.decode(stored, Base64.NO_WRAP);
        try {
            if (payload.length < 13) throw new IllegalArgumentException("Повреждены SSH-данные");
            Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
            cipher.init(Cipher.DECRYPT_MODE, key(),
                new GCMParameterSpec(128, payload, 0, 12));
            return new String(cipher.doFinal(payload, 12, payload.length - 12),
                StandardCharsets.UTF_8);
        } finally { Arrays.fill(payload, (byte) 0); }
    }

    void clearPassword() { clearPassword(TerminalKey.FIRST_VPS); }
    void clearPassword(String id) {
        String s = suffix(id);
        prefs.edit().remove("ssh_password_encrypted" + s).remove("password_host" + s)
            .remove("password_user" + s).commit();
    }

    private SecretKey key() throws Exception {
        KeyStore store = KeyStore.getInstance("AndroidKeyStore");
        store.load(null);
        SecretKey existing = (SecretKey) store.getKey(KEY_ALIAS, null);
        if (existing != null) return existing;
        KeyGenerator generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES,
            "AndroidKeyStore");
        generator.init(new KeyGenParameterSpec.Builder(KEY_ALIAS,
            KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
            .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
            .setKeySize(256).build(), new SecureRandom());
        return generator.generateKey();
    }
}
