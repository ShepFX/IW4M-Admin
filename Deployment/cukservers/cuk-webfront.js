(() => {
    'use strict';

    const serverName = 'Survival Bozos';
    const gameMarker = '[7DTD]';
    let queued = false;

    function replaceTitleMarker(title) {
        const walker = document.createTreeWalker(title, NodeFilter.SHOW_TEXT);
        let node;
        while ((node = walker.nextNode())) {
            if (node.nodeValue.includes(gameMarker)) {
                node.nodeValue = node.nodeValue.replace(/\s*\[7DTD\]/g, '');
            }
        }
    }

    function enhanceServerCards() {
        document.querySelectorAll('[id^="server_header_"]').forEach(header => {
            const title = header.querySelector('h3');
            const existingBadge = header.querySelector('[data-cuk-game="7dtd"]');
            const is7dtd = existingBadge || title?.textContent.includes(gameMarker);
            if (!is7dtd) {
                return;
            }

            const badge = existingBadge || Array.from(header.querySelectorAll('span')).find(element => {
                const value = element.textContent.trim().toUpperCase();
                return value === 'UKN' || value === 'UNKNOWN' || value === 'UNKNOWN GAME';
            });

            if (badge && !existingBadge) {
                badge.dataset.cukGame = '7dtd';
                badge.replaceChildren();
                badge.style.display = 'inline-flex';
                badge.style.alignItems = 'center';

                const icon = document.createElement('i');
                icon.className = 'ph-fill ph-skull';
                icon.style.marginRight = '0.3rem';
                icon.setAttribute('aria-hidden', 'true');

                const label = document.createElement('span');
                label.textContent = '7DTD';
                badge.append(icon, label);
                badge.title = '7 Days to Die';
            }

            if (title) {
                replaceTitleMarker(title);
            }
        });
    }

    function replaceRelatedUnknownLabels() {
        if (!document.body.textContent.includes(serverName)) {
            return;
        }

        document.querySelectorAll('span, p, dt, dd, td').forEach(element => {
            if (element.childElementCount !== 0) {
                return;
            }
            const value = element.textContent.trim().toUpperCase();
            if (value === 'UKN' || value === 'UNKNOWN GAME') {
                element.textContent = '7 Days to Die';
            }
        });
    }

    // ── Zombie badge tiles (Zombie Record Badges plugin) ─────────────────────
    //
    // The plugin can only emit text, so each tile arrives as
    // "Map | <medal> <headline> <detail> | ..." with one segment per placing.
    // Lay that out as a map heading with a trophy row per placing, tint the
    // tile by its best placing, stop the scrolling-text fallback, give each
    // category its own icon and a gold/silver/bronze tally under the heading.
    // High-round boards also badge 4th and 5th, with a medal instead of a trophy.

    const medalPlaces = { '\u{1F947}': 1, '\u{1F948}': 2, '\u{1F949}': 3, '\u{1F396}': 4, '\u{1F3C5}': 5 };
    const medalNames = ['Gold', 'Silver', 'Bronze', '4th place', '5th place'];
    const categoryIcons = {
        'Zombie Statistics': 'ph-skull',
        'Zombie Records': 'ph-trophy',
        'Zombie Speedruns': 'ph-timer',
        'Easter Eggs': 'ph-egg'
    };

    function parseBadgeRows(text) {
        const segments = text.split('|').map(part => part.trim()).filter(Boolean);
        if (segments.length < 2) {
            return null;
        }

        const rows = [];
        for (const segment of segments.slice(1)) {
            const medal = Array.from(segment)[0] || '';
            const place = medalPlaces[medal];
            if (!place) {
                return null;
            }
            const rest = segment.slice(medal.length).trim();
            const space = rest.indexOf(' ');
            rows.push({
                place,
                headline: space > 0 ? rest.slice(0, space) : rest,
                detail: space > 0 ? rest.slice(space + 1).trim() : ''
            });
        }
        return { map: segments[0], rows };
    }

    function enhanceBadgeTile(tile) {
        // Keyed on the DOM rather than a flag: if the webfront re-renders the
        // tile's text, the badge is rebuilt on the next pass.
        const big = tile.querySelector('.meta-value-text');
        if (!big || big.querySelector('.cuk-map')) {
            return null;
        }

        const parsed = parseBadgeRows(big.textContent);
        if (!parsed) {
            return null;
        }

        const best = Math.min(...parsed.rows.map(row => row.place));
        tile.dataset.cukPlace = String(best);

        const heading = document.createElement('span');
        heading.className = 'cuk-map';
        heading.textContent = parsed.map;

        const list = document.createElement('span');
        list.className = 'cuk-rows';
        for (const row of parsed.rows) {
            const line = document.createElement('span');
            line.className = 'cuk-row';
            line.dataset.place = String(row.place);

            const trophy = document.createElement('i');
            trophy.className = row.place <= 3 ? 'ph-fill ph-trophy' : 'ph-fill ph-medal';
            trophy.title = medalNames[row.place - 1];
            trophy.setAttribute('aria-label', medalNames[row.place - 1]);

            const headline = document.createElement('b');
            headline.textContent = row.headline;

            const detail = document.createElement('small');
            detail.textContent = row.detail;

            line.append(trophy, headline, detail);
            list.append(line);
        }

        big.replaceChildren(heading, list);
        big.classList.remove('marquee-enabled');
        big.style.removeProperty('--marquee-duration');

        const card = tile.firstElementChild;
        if (card) {
            card.classList.add('cuk-badge');
        }

        const label = tile.querySelector('.marquee-text');
        const labelBox = label ? label.parentElement : null;
        if (labelBox) {
            labelBox.classList.remove('marquee-enabled');
            labelBox.style.removeProperty('--marquee-duration');
        }

        return parsed.rows;
    }

    function tallyText(rows) {
        const counts = [0, 0, 0];
        let topFive = 0;
        rows.forEach(row => {
            if (row.place <= 3) {
                counts[row.place - 1]++;
            } else {
                topFive++;
            }
        });
        const parts = counts
            .map((count, index) => count ? `${count} ${medalNames[index].toLowerCase()}` : '')
            .filter(Boolean);
        if (topFive) {
            parts.push(`${topFive} top 5`);
        }
        return parts.join(' \u00b7 ');
    }

    function enhanceBadgePanels() {
        document.querySelectorAll('h3').forEach(heading => {
            const icon = categoryIcons[heading.textContent.trim()];
            if (!icon) {
                return;
            }

            const panel = heading.closest('.space-y-3');
            if (!panel) {
                return;
            }

            panel.classList.add('cuk-badge-panel');

            const glyph = panel.querySelector('i.ph');
            if (glyph && !glyph.classList.contains(icon)) {
                glyph.className = 'ph ' + icon + ' text-lg';
            }

            let changed = false;
            panel.querySelectorAll('.tooltip-wrapper').forEach(tile => {
                if (enhanceBadgeTile(tile)) {
                    changed = true;
                }
            });

            // Tally under the heading, rebuilt whenever a tile was (re)built.
            const header = heading.parentElement;
            let tally = panel.querySelector('.cuk-tally');
            if (changed || !tally) {
                const rows = [];
                panel.querySelectorAll('.cuk-row').forEach(row => {
                    rows.push({ place: Number(row.dataset.place) });
                });
                if (!rows.length) {
                    return;
                }
                if (!tally) {
                    tally = document.createElement('div');
                    tally.className = 'cuk-tally';
                    header.after(tally);
                }
                tally.textContent = tallyText(rows);
            }
        });
    }

    // ── Server list: compact empty servers, no K/D on zombies ────────────────
    //
    // Most servers are empty most of the time and each still got a full card
    // with a banner and an empty scoreboard; empty cards shrink to a slim row
    // (user.css) and grow back as soon as someone joins. Zombies scoreboards
    // lose the Kills/Deaths columns, which are always 0 there.

    // "zom" (T4), "Zombies", "Zombies Classic" (Mob of the Dead) ...
    const zombieModes = /^zom/i;

    function tidyServerCards() {
        document.querySelectorAll('[id^="server_header_"]').forEach(header => {
            const card = header.closest('article');
            if (!card) {
                return;
            }

            const count = card.querySelector('span.tabular-nums');
            const empty = count && parseInt(count.textContent, 10) === 0;
            if (empty !== (card.dataset.cukEmpty === '1')) {
                if (empty) {
                    card.dataset.cukEmpty = '1';
                } else {
                    delete card.dataset.cukEmpty;
                }
            }

            const mode = header.querySelector('h3 + div span:last-child');
            if (mode && zombieModes.test(mode.textContent.trim()) && !card.dataset.cukZombies) {
                card.dataset.cukZombies = '1';
            }
        });
    }

    // The scoreboard pop-up on a zombies server: only player, score and ping.
    // K, D and K/D are always 0 there, and Z - the anticheat's score - means
    // nothing for zombies. The pop-up does not say which server it belongs to,
    // so remember which card's Scoreboard button opened it.
    let scoreboardIsZombies = false;
    document.addEventListener('click', event => {
        const button = event.target.closest && event.target.closest('button[title="Scoreboard"]');
        if (button) {
            scoreboardIsZombies = !!button.closest('article[data-cuk-zombies]');
            scheduleEnhance();
        }
    }, true);

    function tidyScoreboard() {
        document.querySelectorAll('.fixed.inset-0 table').forEach(table => {
            const heads = Array.from(table.querySelectorAll('thead th')).map(th => th.textContent.replace(/[▲▼]/g, '').trim());
            if (heads.slice(2, 6).join('|') !== 'K|D|K/D|Z') {
                return;
            }
            if (scoreboardIsZombies !== (table.dataset.cukZombies === '1')) {
                if (scoreboardIsZombies) {
                    table.dataset.cukZombies = '1';
                } else {
                    delete table.dataset.cukZombies;
                }
            }
        });
    }

    // ── Profiles: zombie stats and a link to the stats site ──────────────────
    //
    // A zombies player's Game Statistics panel is all multiplayer numbers:
    // "#-- of 893", 0 kills, 0 deaths, NaN KDR. When there are no multiplayer
    // stats, the tiles show the player's zombies stats from the stats site
    // instead (or the panel hides if there are none of those either).

    const statsSite = 'https://stats.cukservers.net';
    const zombieProfiles = new Map();

    function profileId() {
        const match = location.pathname.match(/^\/client\/(?:profile\/)?(\d+)\/?$/i);
        return match ? match[1] : null;
    }

    function zombieProfile(id) {
        if (!zombieProfiles.has(id)) {
            zombieProfiles.set(id, fetch(`${statsSite}/api/player/${id}`)
                .then(response => response.ok ? response.json() : null)
                .catch(() => null));
        }
        return zombieProfiles.get(id);
    }

    // A zombie-only player has no multiplayer kills or deaths, so the Game
    // Statistics panel would be a grid of zeros. Their numbers live in the
    // Zombie Statistics panel the badge plugin renders, so drop the empty one
    // rather than leaving two stats panels on the page.
    function hideEmptyGameStats(data) {
        const heading = Array.from(document.querySelectorAll('h3'))
            .find(h => h.textContent.trim() === 'Game Statistics');
        const panel = heading ? heading.closest('.space-y-3') : null;
        const grid = panel ? panel.querySelector('.grid') : null;
        if (!grid) {
            return;
        }

        const tiles = Array.from(grid.children);
        const labelOf = tile => (tile.querySelector('.marquee-text')?.textContent || '').trim().toLowerCase();
        const read = label => {
            const tile = tiles.find(t => labelOf(t) === label);
            return tile ? parseInt(tile.querySelector('.meta-value-text')?.textContent, 10) || 0 : null;
        };

        const kills = read('kills');
        if (kills === null || kills > 0 || read('deaths') > 0) {
            return;
        }

        // Only hide it once we know there is a zombies panel to replace it.
        const totals = data && data.totals;
        const hasZombieStats = (data && data.gamestats && data.gamestats.games > 0)
            || (totals && (totals.records || totals.speedruns));
        if (hasZombieStats) {
            panel.dataset.cukHidden = '1';
        }
    }

    function zombieProfileLink(id, data) {
        const viewStats = Array.from(document.querySelectorAll('a[href$="/stats"]'))
            .find(a => a.textContent.trim() === 'View Stats');
        const totals = data && data.totals;
        if (!viewStats || !totals || !(totals.records || totals.speedruns)) {
            return;
        }

        const href = `${statsSite}/player/${id}`;
        let link = viewStats.parentElement.querySelector('[data-cuk-zlink]');
        if (!link) {
            link = document.createElement('a');
            link.dataset.cukZlink = '1';
            link.target = '_blank';
            link.rel = 'noopener';
            link.className = viewStats.className;
            link.title = 'Records, speedruns and VODs on the stats site';
            link.innerHTML = '<i class="ph ph-trophy text-base"></i><span>Zombie Profile</span>';
            viewStats.parentElement.append(link);
        }
        if (link.getAttribute('href') !== href) {
            link.href = href;
        }
    }

    function enhanceProfile() {
        const id = profileId();
        if (!id) {
            return;
        }
        zombieProfile(id).then(data => {
            if (profileId() === id) {
                hideEmptyGameStats(data);
                zombieProfileLink(id, data);
            }
        });
    }

    // ── Sidebar logo: the CUK brand mark instead of a plain "C" ──────────────

    function brandLogo() {
        document.querySelectorAll('a[href="/"] > span:first-child').forEach(mark => {
            if (mark.dataset.cukLogo || mark.textContent.trim() !== 'C') {
                return;
            }
            mark.dataset.cukLogo = '1';
            const img = document.createElement('img');
            img.src = '/brand/icon-512.png?v=20260927b';
            img.alt = '';
            mark.replaceChildren(img);
        });
    }

    // The 7 Days to Die banner is served from the host (nginx) rather than the
    // app image, and the app links it without a version, so bump the URL here
    // to skip any copy cached before the banner changed.
    const bannerVersion = '20260919';

    function versionBanner() {
        document.querySelectorAll('[style*="banners/d7d.jpg"]').forEach(element => {
            const style = element.getAttribute('style');
            if (style && !style.includes('d7d.jpg?v=')) {
                element.setAttribute('style', style.replace('d7d.jpg', 'd7d.jpg?v=' + bannerVersion));
            }
        });
    }

    function enhance() {
        queued = false;
        enhanceServerCards();
        tidyServerCards();
        tidyScoreboard();
        replaceRelatedUnknownLabels();
        enhanceBadgePanels();
        enhanceProfile();
        brandLogo();
        versionBanner();
    }

    function scheduleEnhance() {
        if (!queued) {
            queued = true;
            setTimeout(enhance, 0);
        }
    }

    new MutationObserver(scheduleEnhance).observe(document.documentElement, {
        childList: true,
        subtree: true,
        characterData: true
    });

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', scheduleEnhance, { once: true });
    } else {
        scheduleEnhance();
    }
})();
