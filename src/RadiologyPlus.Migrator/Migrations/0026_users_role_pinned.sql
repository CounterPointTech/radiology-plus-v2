-- 0026: admin-pinned roles for federated (Novarad) users.
--
-- Every Novarad sign-in re-derives the user's role from their Novarad role name
-- (Administrators -> Admin, Radiologists -> Radiologist, otherwise Tech) and writes
-- it back, so an administrator could not promote a Novarad user: the next sign-in
-- undid it. A pinned role is owned by the Radiology Plus administrator and survives
-- sign-in; an unpinned role keeps following Novarad. Local users ignore the flag.

ALTER TABLE identity.users
    ADD COLUMN IF NOT EXISTS role_pinned BOOLEAN NOT NULL DEFAULT FALSE;

COMMENT ON COLUMN identity.users.role_pinned IS
    'TRUE when an administrator set the role explicitly; federated sign-in then leaves role unchanged.';
