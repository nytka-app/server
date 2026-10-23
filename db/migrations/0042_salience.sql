set local lock_timeout = '5s';

-- Second-brain redesign, step 2. How much an extracted item matters, as the model judged it: vital (worth today's
-- screen), ordinary, or noise (kept, never shown first). Null for items made before this column or by hand.
alter table tasks add column salience text null check (salience in ('vital', 'ordinary', 'noise'));
alter table notes add column salience text null check (salience in ('vital', 'ordinary', 'noise'));
alter table memories add column salience text null check (salience in ('vital', 'ordinary', 'noise'));

-- When a memory stops being true (a current place, an illness, a plan with a date): readers leave it out from then on.
-- Null for a lasting fact.
alter table memories add column expires_at timestamptz null;
