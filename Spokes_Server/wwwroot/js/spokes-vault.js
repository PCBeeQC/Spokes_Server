// spokes-vault.js
// Static JS functions for Capacitor vault key persistence.
// Replaces inline eval() calls for reading/writing the encrypted chat password
// to/from native Capacitor Preferences storage.

window.spokesVault = {

    /**
     * Reads the stored encrypted vault key from Capacitor Preferences.
     * Returns { key: string } if found, or null if not available.
     * On web, returns null (vault key comes from HttpOnly cookie, handled server-side).
     */
    getStoredVaultKey: async function () {
        if (window.Capacitor && window.Capacitor.Plugins && window.Capacitor.Plugins.Preferences) {
            try {
                const vaultKey = 'chat_vault_key_' + window.location.hostname;
                let res = await window.Capacitor.Plugins.Preferences.get({ key: vaultKey });

                // Migration: if namespaced key is missing, check legacy global key
                if (!res || !res.value) {
                    res = await window.Capacitor.Plugins.Preferences.get({ key: 'chat_vault_key' });
                    if (res && res.value) {
                        await window.Capacitor.Plugins.Preferences.set({ key: vaultKey, value: res.value });
                        await window.Capacitor.Plugins.Preferences.remove({ key: 'chat_vault_key' });
                    }
                }

                if (res && res.value) {
                    return { key: res.value };
                }
            } catch (e) {
                console.warn('[Vault] Failed to read from Preferences:', e);
            }
            return null;
        }
        // Web: vault key comes from HttpOnly cookie, handled server-side via TryInitializeFromVaultCookie.
        return null;
    },

    /**
     * Stores the encrypted vault key to Capacitor Preferences.
     * Only operates on native mobile platforms.
     * @param {string} encryptedKey - The Data Protection-encrypted vault password.
     */
    storeVaultKey: async function (encryptedKey) {
        if (window.Capacitor && window.Capacitor.Plugins && window.Capacitor.Plugins.Preferences) {
            const key = 'chat_vault_key_' + window.location.hostname;
            await window.Capacitor.Plugins.Preferences.set({ key: key, value: encryptedKey });
        }
    },

    /**
     * Clears all stored vault keys from Capacitor Preferences.
     * Removes both the namespaced key and any legacy global keys.
     */
    clearStoredVaultKey: async function () {
        if (window.Capacitor && window.Capacitor.isNativePlatform()) {
            const vaultKeyHost = 'chat_vault_key_' + window.location.hostname;
            await window.Capacitor.Plugins.Preferences.remove({ key: vaultKeyHost });
            await window.Capacitor.Plugins.Preferences.remove({ key: 'chat_vault_key' });
        }
    }
};
