-- Version 2: the right "Couvrir plusieurs équipes" (vue-ensemble:equipes).
-- Without it an account is bound to one team. Given to the roles created
-- from the direction / responsables templates, which cover every team.
INSERT OR IGNORE INTO RoleGrants (RoleId, Permission)
SELECT Id, 'vue-ensemble:equipes' FROM Roles
WHERE Code IN ('direction', 'resp-production', 'resp-qualite', 'resp-maintenance', 'resp-rh');
