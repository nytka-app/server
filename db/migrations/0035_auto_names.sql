set local lock_timeout = '5s';

-- docs/specs/people.md, Auto-applied names. A suggestion the model was sure of is applied when it is made: status
-- auto_applied, with applied recording what the apply changed so POST /people/suggestions/{id}/undo can put it back.
-- Undo turns the row into rejected, which the unique key keeps from being offered for that voice again.
alter table name_suggestions drop constraint name_suggestions_status_check;
alter table name_suggestions
    add constraint name_suggestions_status_check check (status in ('pending', 'accepted', 'rejected', 'auto_applied')),
    add column applied jsonb null;

create index name_suggestions_auto_applied on name_suggestions (decided_at desc) where status = 'auto_applied';
