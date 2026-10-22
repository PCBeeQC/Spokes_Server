// spokes-vault.js
// Static JS functions for Capacitor and web vault key persistence.
// Centralizes reading/writing the encrypted chat password and calling /spokesapi/vault/* endpoints.

window.spokesVault = {

    /**
     * Reads the stored encrypted vault key from Capacitor Preferences.
     * Returns { key: string } if found, or null if not available.
     * On web, returns null (vault key comes from HttpOnly cookie, handled server-side).
     */
    getStoredVaultKey: async function () {
        if (window.Capacitor?.Plugins?.Preferences) {
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
        if (window.Capacitor?.Plugins?.Preferences) {
            const key = 'chat_vault_key_' + window.location.hostname;
            await window.Capacitor.Plugins.Preferences.set({ key, value: encryptedKey });
        }
    },

    /**
     * Clears all stored vault keys from Capacitor Preferences.
     * Removes both the namespaced key and any legacy global keys.
     */
    clearStoredVaultKey: async function () {
        if (window.Capacitor?.isNativePlatform?.()) {
            const vaultKeyHost = 'chat_vault_key_' + window.location.hostname;
            await window.Capacitor.Plugins.Preferences.remove({ key: vaultKeyHost });
            await window.Capacitor.Plugins.Preferences.remove({ key: 'chat_vault_key' });
        }
    },

    /**
     * Authenticates with /spokesapi/vault/login to set the HttpOnly cookie (on web),
     * mark DeviceSession.HasVaultCookie = true, and persist the encrypted key in
     * Capacitor Preferences (on native mobile).
     * @param {string} password - The user's vault password.
     * @returns {Promise<{success: boolean, vaultKey?: string}>}
     */
    login: async function (password) {
        try {
            const headers = window.spokesAuth?.getHeaders
                ? await window.spokesAuth.getHeaders({ 'Content-Type': 'application/json' })
                : { 'Content-Type': 'application/json' };
            const response = await fetch('/spokesapi/vault/login', {
                method: 'POST',
                headers: headers,
                body: JSON.stringify({ Password: password })
            });
            if (!response.ok) {
                return { success: false };
            }
            const data = await response.json();
            if (data && data.vaultKey) {
                await this.storeVaultKey(data.vaultKey);
            }
            return data;
        } catch (e) {
            console.warn('[Vault] Login failed:', e);
            return { success: false };
        }
    },

    /**
     * Logs out of /spokesapi/vault/logout (deleting the HttpOnly cookie and clearing
     * DeviceSession.HasVaultCookie) and removes any stored Capacitor Preferences key.
     */
    logout: async function () {
        try {
            const headers = window.spokesAuth?.getHeaders
                ? await window.spokesAuth.getHeaders()
                : {};
            await fetch('/spokesapi/vault/logout', {
                method: 'POST',
                headers: headers
            });
        } catch (e) {
            console.warn('[Vault] Logout request failed:', e);
        }
        await this.clearStoredVaultKey();
    },

    /**
     * Queries /spokesapi/vault/status for whether the current device has an active vault cookie.
     * @returns {Promise<{hasCookie: boolean}>}
     */
    getStatus: async function () {
        try {
            const headers = window.spokesAuth?.getHeaders
                ? await window.spokesAuth.getHeaders()
                : {};
            const response = await fetch('/spokesapi/vault/status', {
                credentials: 'include',
                cache: 'no-store',
                headers: headers
            });
            if (response.ok) {
                return await response.json();
            }
        } catch (e) {
            console.warn('[Vault] Status check failed:', e);
        }
        return { hasCookie: false };
    }
};

window.spokes_vault = window.spokesVault;
