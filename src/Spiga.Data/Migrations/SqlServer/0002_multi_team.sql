-- Version 2: the right "Couvrir plusieurs équipes" (vue-ensemble:equipes).
-- Without it an account is bound to one team. Given to the roles created
-- from the direction / responsables templates, which cover every team.
INSERT INTO dbo.RoleGrants (RoleId, Permission)
SELECT r.Id, 'vue-ensemble:equipes' FROM dbo.Roles r
WHERE r.Code IN ('direction', 'resp-production', 'resp-qualite', 'resp-maintenance', 'resp-rh')
  AND NOT EXISTS (SELECT 1 FROM dbo.RoleGrants g WHERE g.RoleId = r.Id AND g.Permission = 'vue-ensemble:equipes');
