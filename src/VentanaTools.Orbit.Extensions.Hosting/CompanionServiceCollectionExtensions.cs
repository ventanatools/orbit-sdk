// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace VentanaTools.Orbit.Extensions.Hosting;

/// <summary>
/// Runs a companion in the .NET Generic Host (contract §9.5): its handler comes from dependency
/// injection, and the companion runs as a hosted service with the same file discovery, status,
/// file watching and exit codes as <see cref="CompanionApp.RunAsync"/>. It logs through
/// <see cref="ILogger"/> instead of writing status lines, and stops when the host stops.
/// </summary>
/// <remarks>
/// <para>
/// A process runs one companion, for one manifest. The host owns Ctrl+C and shutdown: when it
/// stops, the companion's connection closes, its sessions end and their handlers' tokens are
/// cancelled, as when <see cref="CompanionApp.RunAsync"/> is cancelled.
/// </para>
/// <para>
/// The handler lives as long as the companion: <c>AddCompanion&lt;THandler&gt;</c> resolves it once,
/// as a singleton, and the host's start fails with <see cref="InvalidOperationException"/> when
/// <c>THandler</c> is registered with another lifetime, before or after the call (for example the
/// transient typed client <c>AddHttpClient&lt;THandler&gt;()</c> adds). A handler takes shorter-lived
/// services through <c>IHttpClientFactory</c> and <see cref="IServiceScopeFactory"/>, with a scope
/// per session or invocation.
/// </para>
/// <para>
/// <see cref="CompanionServiceOptions"/> follows the options pattern: the delegate given to
/// <c>AddCompanion</c>, <c>services.Configure&lt;CompanionServiceOptions&gt;</c> and configuration
/// binding all apply, in the order they were registered. The options are validated when the host
/// starts, which fails with <see cref="OptionsValidationException"/> for an out-of-range option.
/// </para>
/// <para>
/// Log lines follow the SDK's logging rule: states, reason codes, fixes and help links, never
/// pairing contents, paths, pipe names, setting values or face text. A handler's own exception is
/// logged with its fault, and the host's fixed error text only with <c>--verbose</c>.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var builder = Host.CreateApplicationBuilder(args);
/// builder.Services.AddCompanion&lt;ClockWidget&gt;();
/// await builder.Build().RunAsync();
/// </code>
/// </example>
public static class CompanionServiceCollectionExtensions
{
    /// <summary>
    /// Registers <typeparamref name="THandler"/> as a singleton, unless it is registered already,
    /// and runs the companion with it as a hosted service, with the options configured elsewhere or
    /// their defaults.
    /// </summary>
    /// <typeparam name="THandler">
    /// The contribution handler; its constructor's parameters come from the container. It must be a singleton:
    /// another registered lifetime fails the host's start.
    /// </typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="InvalidOperationException">A companion is registered already.</exception>
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddCompanion<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(
        this IServiceCollection services)
        where THandler : class, IContributionHandler
    {
        ArgumentNullException.ThrowIfNull(services);
        return AddHandler<THandler>(services, null);
    }

    /// <summary>
    /// Registers <typeparamref name="THandler"/> as a singleton, unless it is registered already,
    /// and runs the companion with it as a hosted service.
    /// </summary>
    /// <typeparam name="THandler">
    /// The contribution handler; its constructor's parameters come from the container. It must be a singleton:
    /// another registered lifetime fails the host's start.
    /// </typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Sets the options; registered as a configuration step of <see cref="CompanionServiceOptions"/>.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configure"/> is null.</exception>
    /// <exception cref="InvalidOperationException">A companion is registered already.</exception>
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddCompanion<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(
        this IServiceCollection services, Action<CompanionServiceOptions> configure)
        where THandler : class, IContributionHandler
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        return AddHandler<THandler>(services, configure);
    }

    /// <summary>
    /// Runs the companion as a hosted service with the handler <paramref name="handlerFactory"/> creates, with the options
    /// configured elsewhere or their defaults.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="handlerFactory">Creates the handler from the application's services, once, when the host starts.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="handlerFactory"/> is null.</exception>
    /// <exception cref="InvalidOperationException">A companion is registered already.</exception>
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddCompanion(this IServiceCollection services, Func<IServiceProvider, IContributionHandler> handlerFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(handlerFactory);
        EnsureFirst(services);
        return Register(services, null, handlerFactory);
    }

    /// <summary>Runs the companion as a hosted service with the handler <paramref name="handlerFactory"/> creates.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="handlerFactory">Creates the handler from the application's services, once, when the host starts.</param>
    /// <param name="configure">Sets the options; registered as a configuration step of <see cref="CompanionServiceOptions"/>.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/>, <paramref name="handlerFactory"/> or <paramref name="configure"/> is null.</exception>
    /// <exception cref="InvalidOperationException">A companion is registered already.</exception>
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddCompanion(this IServiceCollection services, Func<IServiceProvider, IContributionHandler> handlerFactory,
        Action<CompanionServiceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(handlerFactory);
        ArgumentNullException.ThrowIfNull(configure);
        EnsureFirst(services);
        return Register(services, configure, handlerFactory);
    }

    private static IServiceCollection AddHandler<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(
        IServiceCollection services, Action<CompanionServiceOptions>? configure)
        where THandler : class, IContributionHandler
    {
        EnsureFirst(services);
        services.TryAddSingleton<THandler>();
        return Register(services, configure, provider =>
        {
            // The handler is resolved once and lives as long as the companion. The registration the container resolves
            // is the last one, made before or after this call; one with a shorter lifetime, such as the transient typed
            // client AddHttpClient<THandler>() adds, would be held for the life of the process, so it is refused.
            var lifetime = services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(THandler) && !descriptor.IsKeyedService)?.Lifetime;
            if (lifetime is { } registered && registered != ServiceLifetime.Singleton)
            {
                throw new InvalidOperationException("The handler " + typeof(THandler).Name + " is registered as " + registered
                    + ", but a companion's handler lives as long as the companion, so it must be a singleton. Remove that registration "
                    + "(AddHttpClient<" + typeof(THandler).Name + ">() adds a transient one), and take shorter-lived services through "
                    + "IHttpClientFactory and IServiceScopeFactory.");
            }

            return provider.GetRequiredService<THandler>();
        });
    }

    private static void EnsureFirst(IServiceCollection services)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(CompanionRegistration)))
        {
            throw new InvalidOperationException("A companion is registered already: a process runs one companion, for one manifest.");
        }
    }

    private static IServiceCollection Register(IServiceCollection services, Action<CompanionServiceOptions>? configure,
        Func<IServiceProvider, IContributionHandler> handlerFactory)
    {
        // The options pattern: Configure<CompanionServiceOptions> and configuration binding apply too, in
        // registration order, and the validator runs when the host starts.
        var options = services.AddOptionsWithValidateOnStart<CompanionServiceOptions, CompanionServiceOptionsValidator>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.AddSingleton(new CompanionRegistration());
        // Arguments are evaluated in order: the options, validated as they are read, come before the handler.
        services.AddHostedService(provider => new CompanionService(
            provider.GetRequiredService<IOptions<CompanionServiceOptions>>().Value,
            handlerFactory(provider) ?? throw new InvalidOperationException("The handler factory returned null."),
            provider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance,
            provider.GetService<IHostApplicationLifetime>()));
        return services;
    }
}

/// <summary>Marks a service collection that has a companion.</summary>
internal sealed class CompanionRegistration
{
}

/// <summary>
/// Checks <see cref="CompanionServiceOptions"/> when the host starts (contract §9.5): the client
/// options are within the ranges <see cref="CompanionClient"/> accepts, and a path, when set, is
/// not empty. Each failure names the option.
/// </summary>
internal sealed class CompanionServiceOptionsValidator : IValidateOptions<CompanionServiceOptions>
{
    public ValidateOptionsResult Validate(string? name, CompanionServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (options.ManifestPath is { } manifest && string.IsNullOrWhiteSpace(manifest))
        {
            failures.Add(nameof(CompanionServiceOptions) + "." + nameof(CompanionServiceOptions.ManifestPath)
                + " is empty: set a path, or leave it null to look for extension.json beside the program.");
        }

        if (options.PairingPath is { } pairing && string.IsNullOrWhiteSpace(pairing))
        {
            failures.Add(nameof(CompanionServiceOptions) + "." + nameof(CompanionServiceOptions.PairingPath)
                + " is empty: set a path, or leave it null to look for the connection info where the host saves it.");
        }

        if (options.Client?.FindProblem() is { } problem)
        {
            failures.Add(nameof(CompanionServiceOptions) + "." + nameof(CompanionServiceOptions.Client) + "." + problem.Option + ": " + problem.Message);
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
