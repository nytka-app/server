set local lock_timeout = '5s';

-- docs/specs/tags.md, Roles. A person known first by what they do (the repairman): named = false, shown by the role's
-- display form ("Repairman", numbered when taken) until a name is said. Renaming the person sets named = true.
alter table people
    add column named boolean not null default true;

-- A suggestion may carry a role. A role-only one stores the role's display form as name and named = false. A target
-- 'person' names the voice of a person known only by role (person_id is that person).
alter table name_suggestions
    add column role  text    null check (role is null or length(role) between 1 and 32),
    add column named boolean not null default true,
    add constraint name_suggestions_named_role check (named or role is not null);

alter table name_suggestions drop constraint name_suggestions_target_check;
alter table name_suggestions
    add constraint name_suggestions_target_check check (target in ('speaker', 'group', 'label', 'person'));

-- The key gains person_id for 'person' targets only, so every existing row keeps its key.
drop index name_suggestions_target_name;
create unique index name_suggestions_target_name on name_suggestions (
    target, (coalesce(speaker_id, group_id::text, case when target = 'person' then person_id::text end, segment_ids[1]::text)),
    lower(name));
