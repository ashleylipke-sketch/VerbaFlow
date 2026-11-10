import { useEffect, useState } from 'react';
import { api, type PlanView } from '../api';

const hours = (m: number) => m >= 60 && m % 60 === 0 ? `${(m / 60).toLocaleString()} hour${m === 60 ? '' : 's'}` : `${m.toLocaleString()} minutes`;
const minutes = (m: number) => Math.round(m) >= 60 ? `${Math.floor(Math.round(m) / 60)} h ${Math.round(m) % 60} min` : `${m.toLocaleString(undefined, { maximumFractionDigits: 1 })} min`;
const when = (s: string) => new Date(s).toLocaleString(undefined, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });
const length = (s: number) => s >= 60 ? `${Math.floor(s / 60)} min ${s % 60} s` : `${s} s`;

/** Your plan (personal accounts) or the organisation's plan (administrators and VerbaFlow support): minutes used, plans, paying. */
export default function PlanPage({ paid, organisation }: { paid?: boolean; organisation?: string }) {
  const [view, setView] = useState<PlanView | null>(null);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState('');
  const [orgPlan, setOrgPlan] = useState('');
  const [customer, setCustomer] = useState('');
  const [saved, setSaved] = useState('');
  const [orgName, setOrgName] = useState('');

  const show = (v: PlanView) => { setView(v); setOrgPlan(v.planId); setCustomer(v.customerId ?? ''); };
  useEffect(() => { api.plan(organisation).then(show).catch(e => setError(e.message)); }, []);
  // VerbaFlow support looks at one organisation at a time: say which.
  useEffect(() => { if (organisation) api.organisations().then(r => setOrgName(r.find(o => o.id === organisation)?.name ?? '')).catch(() => {}); }, []);

  const go = async (what: string, f: () => Promise<{ url: string }>) => {
    setBusy(what); setError('');
    try { location.href = (await f()).url; } catch (e: any) { setError(e.message); setBusy(''); }
  };
  const setOrganisation = async () => {
    setBusy('org'); setError(''); setSaved('');
    try { show(await api.setOrganisationPlan(orgPlan, customer.trim(), organisation)); setSaved('Saved.'); } catch (e: any) { setError(e.message); }
    setBusy('');
  };

  if (!view) return <div className="card"><h2>Plan</h2>{error ? <div className="error" role="alert">{error}</div> : <p className="note">Loading…</p>}</div>;
  const pct = view.minutesPerMonth > 0 ? Math.min(100, view.minutesUsed * 100 / view.minutesPerMonth) : 100;
  const over = view.minutesUsed >= view.minutesPerMonth;
  const barColour = over ? (view.extra && !view.planId.endsWith('trial') ? 'var(--warn)' : 'var(--bad)') : pct >= 80 ? 'var(--warn)' : 'var(--brand)';

  return <>
    <div className="card">
      <h2>{orgName ? `${orgName}’s plan` : view.organisation ? 'Your organisation’s plan' : 'Your plan'}: {view.planName}</h2>
      {organisation && <p><a href="#/organisations">← Organisations</a></p>}
      {paid && <p className="ok" role="status">Thank you. The plan starts as soon as the payment service confirms it, usually within a minute.</p>}
      {error && <div className="error" role="alert">{error}</div>}
      <p>{hours(view.minutesPerMonth)} of speech a month{view.organisation ? ', shared by everyone in the organisation' : ''}. {view.price !== '£0' && view.price}{view.people ? ` · For up to ${view.people} people` : ''}{view.extra ? ` · ${view.extra}, on the next invoice` : ''}</p>
      <p className="note">{view.status}</p>
      <div aria-label="Minutes used this month" role="meter" aria-valuemin={0} aria-valuemax={view.minutesPerMonth} aria-valuenow={view.minutesUsed}
        style={{ height: 12, borderRadius: 6, background: 'var(--line)', overflow: 'hidden', margin: '12px 0 6px' }}>
        <div style={{ width: `${pct}%`, height: '100%', background: barColour }} />
      </div>
      <p><b>{minutes(view.minutesUsed)}</b> used this month{view.minutesLeft > 0 ? `, ${minutes(view.minutesLeft)} left` : ''}. The count starts again on {new Date(view.resetsOn).toLocaleDateString(undefined, { day: 'numeric', month: 'long' })}.
        {view.peopleThisMonth != null && ` ${view.peopleThisMonth} ${view.peopleThisMonth === 1 ? 'person has' : 'people have'} used it this month.`}</p>
      {view.extraMinutes > 0 && <p>Extra this month: <b>{minutes(view.extraMinutes)}</b>{view.extraCharge ? `, ${view.extraCharge} on the next invoice` : ''}.</p>}
      {view.warning && <p className="error" role="status" style={{ borderColor: 'var(--warn)', color: 'var(--warn)' }}>{view.warning}</p>}
      <p className="note">What counts: each recording or imported file turned into text (its length; again when it is converted again), Speech mode while you speak, and the Widget while it listens.</p>
      {view.canManage && <div className="actions"><button onClick={() => go('manage', api.planManage)} disabled={!!busy}>{busy === 'manage' ? 'Opening…' : 'Manage the plan (card, cancel)'}</button></div>}
    </div>

    <div className="card">
      <h2>Plans</h2>
      {view.pretend && !view.organisation && <p className="note">Card payments are not set up yet: choosing a plan opens a pretend payment page, and nothing is charged.</p>}
      {view.organisation && <p className="note">Organisation plans are a monthly subscription, invoiced to the organisation. Minutes over the plan are never refused; they are added to the next month’s invoice. To change the plan, contact VerbaFlow.</p>}
      <div className="scroll"><table className="list">
        <thead><tr><th>Plan</th><th>Minutes a month</th><th>Price</th>{view.organisation && <><th>People</th><th>Extra hours</th></>}<th></th></tr></thead>
        <tbody>{view.plans.map(p => <tr key={p.id}>
          <td data-label="Description"><b>{p.name}</b>{p.current && <span className="tag" style={{ marginLeft: 8 }}>Your plan</span>}</td>
          <td data-label="Minutes">{hours(p.minutesPerMonth)}</td>
          <td data-label="Price">{p.price}</td>
          {view.organisation && <><td data-label="People">{p.people ? `up to ${p.people}` : ''}</td><td data-label="Extra" className="wrap">{p.extra ?? 'not allowed (stops at the limit)'}</td></>}
          <td data-label="Actions">{view.canBuy && !p.current && p.price !== '£0' &&
            <div className="actions"><button className="primary" disabled={!!busy} onClick={() => go(p.id, () => api.planCheckout(p.id))}>{busy === p.id ? 'Opening…' : `Choose ${p.name}`}</button></div>}</td>
        </tr>)}</tbody>
      </table></div>
      {!view.organisation && !view.canBuy && !view.canManage && view.planId !== 'free' && <p className="note">To change plans, use Manage the plan.</p>}
    </div>

    {(view.canSetPlan || view.setInSettings) && view.organisation && <div className="card">
      <h2>Set the organisation’s plan (VerbaFlow support)</h2>
      {view.setInSettings ? <p className="note">The plan is set in VerbaFlow’s settings (Billing:OrganisationPlan), so it cannot be changed here.</p> : <>
        <div className="field"><label htmlFor="org-plan">Plan</label>
          <select id="org-plan" value={orgPlan} onChange={e => setOrgPlan(e.target.value)} style={{ maxWidth: 360 }}>
            {view.plans.map(p => <option key={p.id} value={p.id}>{p.name} ({hours(p.minutesPerMonth)}, {p.price})</option>)}
          </select></div>
        <div className="field"><label htmlFor="org-customer">Customer reference in Stripe (for the invoice, e.g. cus_ABC123)</label>
          <input id="org-customer" value={customer} onChange={e => setCustomer(e.target.value)} style={{ maxWidth: 360 }} /></div>
        <div className="row"><button className="primary" onClick={setOrganisation} disabled={busy === 'org' || !orgPlan}>{busy === 'org' ? 'Saving…' : 'Save'}</button>
          {saved && <span className="ok" role="status"> {saved}</span>}</div>
      </>}
    </div>}

    {view.recent.length > 0 && <div className="card">
      <h2>Recent uses this month</h2>
      <div className="scroll"><table className="list">
        <thead><tr><th>When</th><th>What</th>{view.organisation && <th>Who</th>}<th>Length</th></tr></thead>
        <tbody>{view.recent.map((r, i) => <tr key={i}>
          <td data-label="When">{when(r.at)}</td>
          <td data-label="Description" className="wrap">{r.itemId ? <a href={`#/items/${r.itemId}`}>{r.what}</a> : r.what}</td>
          {view.organisation && <td data-label="Who">{r.who ?? ''}</td>}
          <td data-label="Length">{length(r.seconds)}</td>
        </tr>)}</tbody>
      </table></div>
    </div>}
  </>;
}

/** The stand-in's pretend payment page, shown instead of Stripe while card payments are not set up. */
export function PretendPay({ reference }: { reference: string }) {
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const pay = async () => {
    setBusy(true); setError('');
    try { await api.pretendPay(reference); location.hash = '#/plan?paid=1'; } catch (e: any) { setError(e.message); setBusy(false); }
  };
  return <div className="card" style={{ maxWidth: 520 }}>
    <h2>Pretend payment</h2>
    <p>Card payments are not set up yet, so this page stands in for the payment service. <b>Nothing is charged</b> and no card is asked for.</p>
    {error && <div className="error" role="alert">{error}</div>}
    <div className="actions"><button className="primary" onClick={pay} disabled={busy}>{busy ? 'Paying…' : 'Pretend to pay'}</button> <a className="btn" href="#/plan">Cancel</a></div>
  </div>;
}

/** The stand-in's pretend "manage the plan" page: cancel at the end of the month, take that back, or end now. */
export function PretendManage() {
  const [error, setError] = useState('');
  const [message, setMessage] = useState('');
  const act = async (how: 'end' | 'keep' | 'now', said: string) => {
    setError(''); setMessage('');
    try { await api.pretendCancel(how); setMessage(said); } catch (e: any) { setError(e.message); }
  };
  return <div className="card" style={{ maxWidth: 560 }}>
    <h2>Manage the plan (pretend)</h2>
    <p>Card payments are not set up yet, so this page stands in for the payment service’s own page, where the card is changed or the plan cancelled.</p>
    {error && <div className="error" role="alert">{error}</div>}
    {message && <p className="ok" role="status">{message}</p>}
    <div className="actions" style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
      <button onClick={() => act('end', 'Cancelled: the plan ends at the end of the month.')}>Cancel at the end of the month</button>
      <button onClick={() => act('keep', 'The plan carries on.')}>Keep the plan</button>
      <button className="danger" onClick={() => act('now', 'The plan has ended.')}>End it now</button>
      <a className="btn" href="#/plan">Back to the plan</a>
    </div>
  </div>;
}
