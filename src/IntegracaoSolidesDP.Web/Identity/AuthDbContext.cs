using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace IntegracaoSolidesDP.Web.Identity;

/// <summary>
/// Login (ASP.NET Core Identity) e chaves do Data Protection, no schema próprio
/// <c>solidesdp_auth</c> do bd_rhu_adn. As tabelas são criadas pelas migrations desta pasta.
/// </summary>
public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options)
    : IdentityDbContext<ApplicationUser>(options), IDataProtectionKeyContext
{
    public const string Schema = "solidesdp_auth";

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);

        builder.Entity<ApplicationUser>(user => user.Property(u => u.NomeCompleto).HasMaxLength(120).IsRequired());
    }
}
