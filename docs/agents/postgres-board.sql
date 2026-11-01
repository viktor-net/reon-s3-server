-- Опциональная схема для координации агентов.
-- ВАЖНО: это не часть приложения reon-s3-server и не применяется автоматически.
-- Использовать только если файловой доски и git уже не хватает (см. AGENTS.md §8.4).
-- Не размещать в базе приложения `reon`: отдельная БД или отдельная схема.

create schema if not exists agents;

create table if not exists agents.tasks (
    id            text primary key,
    status        text not null
                  check (status in ('ready', 'claimed', 'mapping', 'verifying', 'reducing', 'done', 'blocked')),
    brief_path    text not null,
    branch        text,
    worktree      text,
    owner_session text,
    owner_model   text,
    files         text[] not null default '{}',
    attempt       integer not null default 0,
    lease_until   timestamptz,
    heartbeat_at  timestamptz,
    created_at    timestamptz not null default now(),
    updated_at    timestamptz not null default now()
);

create index if not exists ix_agents_tasks_claim
    on agents.tasks (status, lease_until);

create table if not exists agents.journal (
    id        bigserial primary key,
    task_id   text not null references agents.tasks (id),
    at        timestamptz not null default now(),
    event     text not null,
    session   text,
    model     text,
    attempt   integer,
    payload   jsonb not null default '{}'::jsonb
);

create index if not exists ix_agents_journal_task_at
    on agents.journal (task_id, at);

-- Атомарный захват одной задачи.
-- $1 — session, $2 — model, $3 — task id, $4 — lease TTL.
update agents.tasks
set status       = 'claimed',
    owner_session = $1,
    owner_model   = $2,
    lease_until   = now() + ($4::text)::interval,
    heartbeat_at  = now(),
    attempt       = attempt + 1,
    updated_at    = now()
where id = $3
  and status <> 'done'
  and (owner_session = $1 or lease_until is null or lease_until < now())
returning *;

-- Выбор следующей свободной задачи без гонки (для пула воркеров).
-- Выполнять в транзакции вместе с update выше.
select *
from agents.tasks
where status = 'ready'
  and (lease_until is null or lease_until < now())
order by created_at
for update skip locked
limit 1;

-- Heartbeat: продлевает lease только текущему владельцу.
update agents.tasks
set heartbeat_at = now(),
    lease_until  = now() + interval '30 minutes',
    updated_at   = now()
where id = $1
  and owner_session = $2
returning *;

-- Reclaim просроченной задачи с записью причины в журнал.
with reclaimed as (
    update agents.tasks
    set owner_session = $1,
        owner_model   = $2,
        lease_until   = now() + interval '30 minutes',
        heartbeat_at  = now(),
        attempt       = attempt + 1,
        updated_at    = now()
    where id = $3
      and lease_until < now()
      and status <> 'done'
    returning id
)
insert into agents.journal (task_id, event, session, model, payload)
select id, 'reclaim', $1, $2, jsonb_build_object('reason', $4)
from reclaimed;
