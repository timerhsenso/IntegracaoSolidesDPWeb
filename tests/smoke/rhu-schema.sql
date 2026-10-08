-- Subconjunto do schema do RHSenso (bd_rhu_adn) lido pelo worker, com os tipos reais das colunas.
-- Usado pelos testes (Testcontainers) e pelos smoke tests dos pacotes no CI.
CREATE TABLE dbo.tsitu1 (cdsituacao char(2) NOT NULL PRIMARY KEY, dcsituacao varchar(40) NULL, fldemissao char(1) NULL);
CREATE TABLE dbo.tcus1 (cdccusto char(5) NOT NULL PRIMARY KEY, dcccusto varchar(100) NOT NULL);
CREATE TABLE dbo.cargo1 (cdcargo char(5) NOT NULL PRIMARY KEY, dccargo varchar(40) NOT NULL, cdcbo6 char(6) NULL);
CREATE TABLE dbo.test1 (cdempresa int NOT NULL, cdfilial int NOT NULL, nmfantasia varchar(30) NULL, dcestab varchar(60) NULL, cdcgc char(15) NULL,
                        PRIMARY KEY (cdempresa, cdfilial));
CREATE TABLE dbo.func1 (
    id uniqueidentifier NOT NULL PRIMARY KEY, nomatric char(8) NOT NULL, nmcolab char(60) NULL, cdempresa int NOT NULL, cdfilial int NOT NULL,
    cdccusto char(5) NULL, tpcolab int NULL, dtdemissao datetime NULL, dtadmissao datetime NOT NULL, dttransf datetime NULL,
    cdcausres char(2) NULL, nocpf varchar(11) NULL, nopis varchar(11) NULL, nocartprof varchar(10) NULL, noserie char(5) NULL,
    dtnasc datetime NULL, cdsexo char(1) NULL, cdestcivil char(1) NULL, cdinstruc char(2) NULL, cod_raca int NULL,
    dcemail varchar(80) NULL, emailalternativo varchar(60) NULL, noddd varchar(3) NULL, notelefone varchar(30) NULL,
    nmmaecolab varchar(60) NULL, nmpaicolab varchar(60) NULL, cdcargo char(5) NULL, cdsituacao char(2) NULL);
CREATE TABLE dbo.feria2 (
    id uniqueidentifier NOT NULL PRIMARY KEY, nomatric char(8) NOT NULL, cdempresa int NOT NULL, cdfilial int NOT NULL,
    dtinipa datetime NULL, dtinipf datetime NOT NULL, dtfimpf datetime NULL, qtdiasfe int NULL, qtabono int NULL, flconfirm int NULL);

INSERT INTO dbo.tsitu1 (cdsituacao, dcsituacao, fldemissao) VALUES
    ('01','ATIVO','N'), ('02','AUXILIO DOENCA','N'), ('08','DEMITIDO','S'), ('09','TRANSFERIDO','N'), ('99','PRE CADASTRO','N');

