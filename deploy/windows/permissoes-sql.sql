/*
    IntegracaoSolidesDP — logins e permissões no SQL Server (privilégio mínimo)

    Rode no SSMS conectado como sysadmin (ou com ALTER ANY LOGIN e db_owner no banco do RHSenso).
    Antes de rodar:
      1. troque as duas senhas logo abaixo;
      2. se o banco não se chamar bd_rhu_adn, troque o USE bd_rhu_adn mais abaixo.
    Pode rodar de novo: não recria o que já existe e não troca a senha de um login existente.

    Quem pode o quê:
      integracao_solidesdp      (serviço)  lê o RHSenso (dbo); é dono do schema solidesdp, onde cria e atualiza
                                           as próprias tabelas.
      integracao_solidesdp_web  (Web/IIS)  lê o RHSenso e o schema solidesdp; grava em solidesdp (configuração,
                                           pedidos ao serviço, tokens e auditoria), sem criar nem apagar tabelas
                                           nem linhas; é dono do schema solidesdp_auth (login da Web), onde as
                                           migrations criam as tabelas.
    Nenhum dos dois grava no dbo.

    Autenticação do Windows em vez de senha: crie os logins FROM WINDOWS (serviço: a conta do serviço; Web: a
    identidade do app pool — "IIS AppPool\IntegracaoSolidesDP.Web" se o SQL estiver no mesmo servidor, ou a conta
    do computador "DOMINIO\SERVIDOR$" se estiver em outro), troque os nomes abaixo e use Integrated Security=True.
*/

USE master;
GO

DECLARE @SenhaServico nvarchar(128) = N'<senha forte do serviço>';
DECLARE @SenhaWeb     nvarchar(128) = N'<senha forte da Web>';

IF @SenhaServico LIKE N'<%' OR @SenhaWeb LIKE N'<%'
BEGIN
    RAISERROR(N'Troque as senhas no início do script antes de rodar. Nada foi alterado.', 16, 1);
    SET NOEXEC ON;  -- os próximos blocos não são executados nesta sessão
END
ELSE
BEGIN
    DECLARE @sql nvarchar(max);

    IF SUSER_ID(N'integracao_solidesdp') IS NULL
    BEGIN
        SET @sql = N'CREATE LOGIN integracao_solidesdp WITH PASSWORD = N''' + REPLACE(@SenhaServico, N'''', N'''''') + N''', CHECK_POLICY = ON;';
        EXEC (@sql);
        PRINT N'Login integracao_solidesdp criado.';
    END;

    IF SUSER_ID(N'integracao_solidesdp_web') IS NULL
    BEGIN
        SET @sql = N'CREATE LOGIN integracao_solidesdp_web WITH PASSWORD = N''' + REPLACE(@SenhaWeb, N'''', N'''''') + N''', CHECK_POLICY = ON;';
        EXEC (@sql);
        PRINT N'Login integracao_solidesdp_web criado.';
    END;
END;
GO

USE bd_rhu_adn;
GO

/* ---------------------------------------------------------------- serviço */

IF USER_ID(N'integracao_solidesdp') IS NULL
    CREATE USER integracao_solidesdp FOR LOGIN integracao_solidesdp;
ALTER ROLE db_datareader ADD MEMBER integracao_solidesdp;
GRANT CREATE TABLE TO integracao_solidesdp;

IF SCHEMA_ID(N'solidesdp') IS NULL
    EXEC (N'CREATE SCHEMA solidesdp AUTHORIZATION integracao_solidesdp');
ELSE IF (SELECT principal_id FROM sys.schemas WHERE name = N'solidesdp') <> USER_ID(N'integracao_solidesdp')
BEGIN
    -- Atenção: a troca de dono apaga as permissões concedidas no schema; a seção da Web, abaixo, concede de novo.
    ALTER AUTHORIZATION ON SCHEMA::solidesdp TO integracao_solidesdp;
    PRINT N'Schema solidesdp passou a pertencer a integracao_solidesdp.';
END;
GO

/* ---------------------------------------------------------------- Web (IIS) */

IF USER_ID(N'integracao_solidesdp_web') IS NULL
    CREATE USER integracao_solidesdp_web FOR LOGIN integracao_solidesdp_web;
ALTER ROLE db_datareader ADD MEMBER integracao_solidesdp_web;
GRANT SELECT, INSERT, UPDATE ON SCHEMA::solidesdp TO integracao_solidesdp_web;
GRANT CREATE TABLE TO integracao_solidesdp_web;

IF SCHEMA_ID(N'solidesdp_auth') IS NULL
    EXEC (N'CREATE SCHEMA solidesdp_auth AUTHORIZATION integracao_solidesdp_web');
ELSE IF (SELECT principal_id FROM sys.schemas WHERE name = N'solidesdp_auth') <> USER_ID(N'integracao_solidesdp_web')
BEGIN
    ALTER AUTHORIZATION ON SCHEMA::solidesdp_auth TO integracao_solidesdp_web;
    PRINT N'Schema solidesdp_auth passou a pertencer a integracao_solidesdp_web.';
END;
GO

-- O login da Web (usuários, senhas, chaves do cookie) não é lido pelo serviço. db_datareader daria leitura a tudo.
DENY SELECT ON SCHEMA::solidesdp_auth TO integracao_solidesdp;
GO

/* ---------------------------------------------------------------- conferência */

SELECT usuario = dp.name,
       papel   = r.name
FROM sys.database_principals dp
LEFT JOIN sys.database_role_members rm ON rm.member_principal_id = dp.principal_id
LEFT JOIN sys.database_principals r ON r.principal_id = rm.role_principal_id
WHERE dp.name IN (N'integracao_solidesdp', N'integracao_solidesdp_web');

SELECT [schema] = s.name, dono = USER_NAME(s.principal_id)
FROM sys.schemas s
WHERE s.name IN (N'solidesdp', N'solidesdp_auth');

SET NOEXEC OFF;
GO
