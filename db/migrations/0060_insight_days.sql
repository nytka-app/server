set local lock_timeout = '5s';

-- Insights ("Over time", NYTKA-147): one row per insight per local day, from the nightly
-- measure-insights job (no model call). A later run of a date replaces its rows.
create table insight_days (
    insight_id  text             not null,
    local_date  date             not null,
    value       double precision not null,
    measured_at timestamptz      not null,
    primary key (insight_id, local_date)
);
