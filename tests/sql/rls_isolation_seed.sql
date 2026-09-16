grant connect on database cubeidentity to cube_app;

-- Two customers, as in the design docs.
insert into identity.organizations (id, slug, display_name, status, authentication_mode, suspension_reason) values
  ('00000000-0000-0000-0000-0000000000a1', 'acme-pharma',       'Acme Pharmaceuticals GmbH', 'active',    'federated', null),
  ('00000000-0000-0000-0000-0000000000b2', 'northwind-devices', 'Northwind Medical Devices', 'active',    'local',     null),
  ('00000000-0000-0000-0000-0000000000c3', 'suspended-co',      'Suspended Co',              'suspended', 'local',     'quarterly suspension drill');

insert into identity.org_domains (domain, org_id, mode, idp_ref, verified, verification_ref) values
  ('acme-pharma.com',       '00000000-0000-0000-0000-0000000000a1', 'federated', 'idp-acme-pharma-saml', true, 'DNS-TXT-1'),
  ('northwind-devices.com', '00000000-0000-0000-0000-0000000000b2', 'local',     null,                   false, null);

insert into identity.users (id, issuer, subject_oid, email) values
  ('00000000-0000-0000-0000-0000000000f1', 'https://cuberm.ciamlogin.com/t/v2.0', 'oid-acme-1',  'j.smith@acme-pharma.com'),
  ('00000000-0000-0000-0000-0000000000f2', 'https://cuberm.ciamlogin.com/t/v2.0', 'oid-north-1', 'a.jones@northwind-devices.com'),
  -- Same oid value, different issuer: proves the composite key is doing work.
  ('00000000-0000-0000-0000-0000000000f3', 'https://login.microsoftonline.com/x/v2.0', 'oid-acme-1', 'dual@acme-pharma.com');

insert into identity.org_memberships (user_id, org_id, user_type) values
  ('00000000-0000-0000-0000-0000000000f1', '00000000-0000-0000-0000-0000000000a1', 'member'),
  ('00000000-0000-0000-0000-0000000000f2', '00000000-0000-0000-0000-0000000000b2', 'member'),
  -- The multi-org consultant from §1.7.
  ('00000000-0000-0000-0000-0000000000f3', '00000000-0000-0000-0000-0000000000a1', 'member'),
  ('00000000-0000-0000-0000-0000000000f3', '00000000-0000-0000-0000-0000000000b2', 'org_admin');
