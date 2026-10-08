-- Dados sintéticos para o smoke test dos pacotes: 2 colaboradores ativos (CLT e estagiário),
-- 1 desligado histórico (não deve ser enviado), 1 autônomo (fora do escopo) e 1 férias liberada.
-- O CNPJ é um dos semeados no fake, então a empresa é resolvida.
INSERT INTO dbo.cargo1 (cdcargo, dccargo, cdcbo6) VALUES ('00100', 'ANALISTA DE SISTEMAS', '212405'), ('00200', 'ESTAGIARIO TI', NULL);
INSERT INTO dbo.test1 (cdempresa, cdfilial, nmfantasia, dcestab, cdcgc) VALUES (1, 1, 'ADN MATRIZ', 'ADN TECNOLOGIA DE SISTEMAS SALVADOR', '00594807000108');
INSERT INTO dbo.tcus1 (cdccusto, dcccusto) VALUES ('00080', 'Despesas Corporativas');

INSERT INTO dbo.func1 (id, nomatric, nmcolab, cdempresa, cdfilial, cdccusto, tpcolab, dtadmissao, nocpf, nopis, dtnasc, cdsexo, cdestcivil, cdinstruc, cod_raca, dcemail, cdcargo, cdsituacao, dtdemissao, cdcausres)
VALUES
  ('11111111-1111-1111-1111-111111111111', '00000001', 'MARIA DA SILVA', 1, 1, '00080', 1, '2020-03-02', '52998224725', '12345678919', '1990-05-17', 'F', 'S', '09', 8, 'maria@exemplo.com.br', '00100', '01', NULL, NULL),
  ('22222222-2222-2222-2222-222222222222', '00000002', 'JOAO SANTOS',    1, 1, '00080', 2, '2026-02-01', '11144477735', NULL,          '2004-01-10', 'M', 'S', '08', 2, 'joao@exemplo.com.br',  '00200', '01', NULL, NULL),
  ('33333333-3333-3333-3333-333333333333', '00000003', 'ANTIGO DEMITIDO', 1, 1, '00080', 1, '2010-01-04', '39053344705', NULL,         '1980-01-01', 'M', 'C', '07', 9, NULL,                   '00100', '08', '2015-06-30', '11'),
  ('44444444-4444-4444-4444-444444444444', '00000004', 'AUTONOMO',       1, 1, '00080', 14, '2024-01-01', NULL,         NULL,          NULL,         NULL, NULL, NULL, NULL, NULL,                 '00100', '01', NULL, NULL);

INSERT INTO dbo.feria2 (id, nomatric, cdempresa, cdfilial, dtinipa, dtinipf, dtfimpf, qtdiasfe, qtabono, flconfirm)
VALUES ('55555555-5555-5555-5555-555555555555', '00000001', 1, 1, '2025-03-02', DATEADD(day, 10, CAST(GETDATE() AS date)), DATEADD(day, 24, CAST(GETDATE() AS date)), 15, 0, 2);
