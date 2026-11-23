// Spokes client-side voice speaking indicator DOM controller.
// Single source of truth for speaking highlights across Chat stage tiles, ChatSidebar avatars,
// GlobalVoiceBar speaker counts, VoicePipOverlay indicators, and VoiceSettingsDialog cards.
// Uses Blazor-untouched `data-voice-speaking="true"` attributes so speech activity toggles never
// trigger full Blazor component RenderTree diffs or SignalR RenderBatch storms.
(function () {
    'use strict';

    if (typeof window === 'undefined' || window.spokesVoiceSpeaking) {
        return;
    }

    /** @type {Map<string, Set<string>>} */
    const channelSpeakers = new Map();
    let observer = null;
    let pendingApply = false;
    let hadActiveSpeakers = false;

    function escapeAttrValue(value) {
        if (typeof CSS !== 'undefined' && typeof CSS.escape === 'function') {
            return CSS.escape(value);
        }
        return String(value).replace(/\\/g, '\\\\').replace(/"/g, '\\"');
    }

    function setSpeakingAttr(el, isSpeaking) {
        if (!el) return;
        const currently = el.getAttribute('data-voice-speaking') === 'true';
        if (isSpeaking && !currently) {
            el.setAttribute('data-voice-speaking', 'true');
        } else if (!isSpeaking && currently) {
            el.removeAttribute('data-voice-speaking');
        }
    }

    function applyElementState(el) {
        if (!el || el.nodeType !== 1) return;

        const speakerChannel = el.getAttribute('data-voice-speaker-channel');
        if (speakerChannel) {
            const userId = el.getAttribute('data-voice-speaker-user');
            if (userId) {
                const isMuted = el.getAttribute('data-voice-muted') === 'true';
                const set = channelSpeakers.get(speakerChannel);
                const isSpeaking = !isMuted && Boolean(set && set.has(userId));
                setSpeakingAttr(el, isSpeaking);
            }
        }

        const anyChannel = el.getAttribute('data-voice-channel-any-speaking');
        if (anyChannel) {
            const set = channelSpeakers.get(anyChannel);
            setSpeakingAttr(el, Boolean(set && set.size > 0));
        }

        const countChannel = el.getAttribute('data-voice-speaker-count');
        if (countChannel) {
            const set = channelSpeakers.get(countChannel);
            const nextText = String(set ? set.size : 0);
            if (el.textContent !== nextText) {
                el.textContent = nextText;
            }
        }
    }

    function applyChannelToDom(channelId) {
        if (typeof document === 'undefined' || !channelId) return;
        const escaped = escapeAttrValue(channelId);
        const set = channelSpeakers.get(channelId);
        const count = set ? set.size : 0;

        const userNodes = document.querySelectorAll(`[data-voice-speaker-channel="${escaped}"][data-voice-speaker-user]`);
        for (let i = 0; i < userNodes.length; i++) {
            const el = userNodes[i];
            const userId = el.getAttribute('data-voice-speaker-user');
            const isMuted = el.getAttribute('data-voice-muted') === 'true';
            const isSpeaking = !isMuted && Boolean(set && userId && set.has(userId));
            setSpeakingAttr(el, isSpeaking);
        }

        const anyNodes = document.querySelectorAll(`[data-voice-channel-any-speaking="${escaped}"]`);
        for (let i = 0; i < anyNodes.length; i++) {
            setSpeakingAttr(anyNodes[i], count > 0);
        }

        const countNodes = document.querySelectorAll(`[data-voice-speaker-count="${escaped}"]`);
        const countText = String(count);
        for (let i = 0; i < countNodes.length; i++) {
            if (countNodes[i].textContent !== countText) {
                countNodes[i].textContent = countText;
            }
        }
    }

    function applyAllToDom() {
        if (typeof document === 'undefined') return;
        const nodes = document.querySelectorAll('[data-voice-speaker-channel], [data-voice-channel-any-speaking], [data-voice-speaker-count]');
        for (let i = 0; i < nodes.length; i++) {
            applyElementState(nodes[i]);
        }
        hadActiveSpeakers = channelSpeakers.size > 0;
    }

    function scheduleDomReconcile() {
        if (pendingApply) return;
        pendingApply = true;
        queueMicrotask(() => {
            pendingApply = false;
            applyAllToDom();
        });
    }

    function ensureObserver() {
        if (observer || typeof MutationObserver === 'undefined' || typeof document === 'undefined' || !document.body) {
            return;
        }
        observer = new MutationObserver((mutations) => {
            if (channelSpeakers.size === 0 && !hadActiveSpeakers) {
                return;
            }
            for (let i = 0; i < mutations.length; i++) {
                const m = mutations[i];
                if (m.type === 'attributes') {
                    applyElementState(m.target);
                    continue;
                }
                if (m.addedNodes && m.addedNodes.length > 0) {
                    for (let j = 0; j < m.addedNodes.length; j++) {
                        const node = m.addedNodes[j];
                        if (node.nodeType !== 1) continue;
                        if (node.hasAttribute &&
                            (node.hasAttribute('data-voice-speaker-channel') ||
                             node.hasAttribute('data-voice-channel-any-speaking') ||
                             node.hasAttribute('data-voice-speaker-count'))) {
                            scheduleDomReconcile();
                            return;
                        }
                        if (node.querySelector &&
                            node.querySelector('[data-voice-speaker-channel], [data-voice-channel-any-speaking], [data-voice-speaker-count]')) {
                            scheduleDomReconcile();
                            return;
                        }
                    }
                }
            }
        });
        observer.observe(document.body, {
            childList: true,
            subtree: true,
            attributes: true,
            attributeFilter: [
                'data-voice-muted',
                'data-voice-speaker-channel',
                'data-voice-speaker-user',
                'data-voice-channel-any-speaking',
                'data-voice-speaker-count'
            ]
        });
    }

    function syncChannel(channelId, speakerIds) {
        if (!channelId || typeof channelId !== 'string') return;
        ensureObserver();
        const nextSet = new Set();
        if (Array.isArray(speakerIds)) {
            for (let i = 0; i < speakerIds.length; i++) {
                const id = speakerIds[i];
                if (typeof id === 'string' && id.length > 0) {
                    nextSet.add(id);
                }
            }
        }
        if (nextSet.size > 0) {
            channelSpeakers.set(channelId, nextSet);
            hadActiveSpeakers = true;
        } else {
            channelSpeakers.delete(channelId);
        }
        applyChannelToDom(channelId);
    }

    function syncChannels(channelMap) {
        if (!channelMap || typeof channelMap !== 'object') return;
        const keys = Object.keys(channelMap);
        for (let i = 0; i < keys.length; i++) {
            syncChannel(keys[i], channelMap[keys[i]]);
        }
    }

    function setUserSpeaking(channelId, userId, isSpeaking) {
        if (!channelId || !userId) return;
        ensureObserver();
        let set = channelSpeakers.get(channelId);
        if (isSpeaking) {
            if (!set) {
                set = new Set();
                channelSpeakers.set(channelId, set);
            }
            if (set.has(userId)) return;
            set.add(userId);
            hadActiveSpeakers = true;
        } else {
            if (!set || !set.has(userId)) return;
            set.delete(userId);
            if (set.size === 0) {
                channelSpeakers.delete(channelId);
            }
        }
        applyChannelToDom(channelId);
    }

    function clearChannel(channelId) {
        syncChannel(channelId, []);
    }

    function getChannelSpeakers(channelId) {
        const set = channelSpeakers.get(channelId);
        return set ? Array.from(set) : [];
    }

    window.spokesVoiceSpeaking = {
        syncChannel,
        syncChannels,
        setUserSpeaking,
        clearChannel,
        applyAll: applyAllToDom,
        getChannelSpeakers
    };

    if (typeof document !== 'undefined') {
        if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', ensureObserver, { once: true });
        } else {
            ensureObserver();
        }
    }
})();
