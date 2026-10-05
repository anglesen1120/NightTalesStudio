import { expectA11yClean } from './fixtures/axe';
import { expect, test } from './fixtures/csp.fixture';
import { episodeDetail, EPISODE_ID, expectNoExtraDetails, NEW_EPISODE_PATH, SERIES_ID, stubEpisodes, watchPrivacy } from './fixtures/episodes-stubs';
import { liveRegion } from './fixtures/series-stubs';

// 10 CR-48: use the real live subscribeEpisode page, SignalR JSON transport and app reconnect ladder.
// No compose stack this wave: reuse boot-refresh/MFA-owner session stubs (episodes-list/shell precedent).
// UI TOTP login + real hub/server reconnect remains compose-stack coverage owned by 11.5.1-T1.
test.use({ locale: 'vi-VN', timezoneId: 'UTC' });
const L = { brief: 'Ý tưởng', submit: 'Viết kịch bản', queued: 'Đang xếp hàng', writing: 'Đang viết kịch bản…', checking: 'Đang kiểm tra…' };
const BRIEF = 'Một chiếc radio cũ phát những câu chuyện chưa từng được kể.';

test.describe('10 CR-48 realtime generation page (stubbed)', () => {
  test('one ticket per connection, offline return resyncs once, burst coalesces and secrets stay private', async ({ page, context }) => {
    let negotiateRequests = 0;
    page.on('request', (request) => {
      if (/\/negotiate(?:\?|$)/.test(new URL(request.url()).pathname)) negotiateRequests += 1;
    });
    const privacy = watchPrivacy(page, BRIEF);
    const stubs = await stubEpisodes(page, { details: [episodeDetail(), episodeDetail('writing'), episodeDetail('checking')] });
    await page.goto(NEW_EPISODE_PATH);
    await expect(page.getByRole('textbox', { name: L.brief, exact: true })).toBeVisible();
    await expect.poll(stubs.connected).toBe(1);
    expect(stubs.entries('ticket')).toHaveLength(1);
    expect(stubs.entries('ticket')[0]!.method).toBe('POST');
    expect(stubs.entries('websocket')).toHaveLength(1);
    expect(negotiateRequests).toBe(0);
    await expectA11yClean(page);
    await page.getByRole('textbox', { name: L.brief, exact: true }).fill(BRIEF);
    await page.getByRole('button', { name: L.submit, exact: true }).click();
    await expect(liveRegion(page, L.queued)).toBeVisible();
    await expectNoExtraDetails(page, stubs, 1);
    await expectA11yClean(page);

    // RealtimeService spaces connect resyncs by 5 s. Wait through that window before forcing a return;
    // otherwise a valid trailing resync would race this count assertion. This is not a progress poll.
    await page.waitForTimeout(5100);
    expect(stubs.entries('detail')).toHaveLength(1);
    stubs.advance();
    // Mock sockets do not necessarily react to network emulation. Exercise the requested offline/online
    // transition AND deterministically emulate the server close (1011) while offline. No test hooks in app.
    await context.setOffline(true);
    stubs.close();
    await context.setOffline(false);
    await expect.poll(stubs.connected, { timeout: 12_000 }).toBe(2);
    await expect(liveRegion(page, L.writing)).toBeVisible();
    expect(stubs.entries('close')).toHaveLength(1);
    expect(stubs.entries('ticket')).toHaveLength(2);
    expect(stubs.entries('ticket').every((entry) => entry.method === 'POST')).toBe(true);
    expect(stubs.entries('websocket')).toHaveLength(2);
    expect(stubs.entries('detail')).toHaveLength(2);
    expect(stubs.entries('detail')[1]!.order).toBeGreaterThan(stubs.entries('websocket')[1]!.order);
    expect(negotiateRequests).toBe(0);
    await expectNoExtraDetails(page, stubs, 2);
    await expectA11yClean(page);
    await privacy();

    stubs.advance();
    // All three frames are sent synchronously, therefore well inside the 250 ms subscription window.
    stubs.push({ episodeId: EPISODE_ID, seriesId: SERIES_ID });
    stubs.push({ episodeId: EPISODE_ID });
    stubs.push({ episodeId: EPISODE_ID, seriesId: SERIES_ID });
    await expect(liveRegion(page, L.checking)).toContainText(L.checking);
    await expect(liveRegion(page, L.checking)).toHaveAttribute('aria-live', 'polite');
    expect(stubs.entries('event')).toHaveLength(3);
    expect(stubs.entries('detail')).toHaveLength(3);
    await expectNoExtraDetails(page, stubs, 3);
    expect(stubs.entries('ticket')).toHaveLength(2);
    expect(stubs.entries('websocket')).toHaveLength(2);
    expect(stubs.entries('create')).toHaveLength(1);
    expect(stubs.entries('generate')).toHaveLength(1);
    expect(negotiateRequests).toBe(0);
    await expectA11yClean(page);
    await privacy();
    // csp.fixture auto-checks every state and connect-src 'self' for the same-origin mocked WS handshake.
  });
});
