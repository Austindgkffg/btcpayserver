﻿#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client.App.Models;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Plugins.PointOfSale;
using BTCPayServer.Services;
using BTCPayServer.Services.Apps;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NicolasDorier.RateLimits;
using PosViewType = BTCPayServer.Plugins.PointOfSale.PosViewType;
using SignInResult = Microsoft.AspNetCore.Identity.SignInResult;

namespace BTCPayServer.App.API;

public partial class AppApiController
{
    private const string Scheme = AuthenticationSchemes.GreenfieldBearer;
    
    [AllowAnonymous]
    [HttpPost("register")]
    [RateLimitsFilter(ZoneLimits.Login, Scope = RateLimitsScope.RemoteAddress)]
    public async Task<Results<Ok<AccessTokenResponse>, Ok<ApplicationUserData>, EmptyHttpResult, ProblemHttpResult>> Register(CreateApplicationUserRequest signup)
    {
        var policies = await settingsRepository.GetSettingAsync<PoliciesSettings>() ?? new PoliciesSettings();
        if (policies.LockSubscription)
            return TypedResults.Problem("This instance does not allow public user registration", statusCode: 401);
            
        var errorMessage = "Invalid signup attempt.";
        if (ModelState.IsValid)
        {
            var isFirstAdmin = !(await userManager.GetUsersInRoleAsync(Roles.ServerAdmin)).Any();
            var user = new ApplicationUser
            {
                UserName = signup.Email,
                Email = signup.Email,
                RequiresEmailConfirmation = policies.RequiresConfirmedEmail,
                RequiresApproval = policies.RequiresUserApproval,
                Created = DateTimeOffset.UtcNow,
                Approved = isFirstAdmin // auto-approve first admin and users created by an admin
            };
            
            var result = await userManager.CreateAsync(user, signup.Password);
            if (result.Succeeded)
            {
                if (isFirstAdmin)
                {
                    await roleManager.CreateAsync(new IdentityRole(Roles.ServerAdmin));
                    await userManager.AddToRoleAsync(user, Roles.ServerAdmin);
                    var settings = await settingsRepository.GetSettingAsync<ThemeSettings>() ?? new ThemeSettings();
                    if (settings.FirstRun)
                    {
                        settings.FirstRun = false;
                        await settingsRepository.UpdateSetting(settings);
                    }

                    await settingsRepository.FirstAdminRegistered(policies, btcpayOptions.UpdateUrl != null, btcpayOptions.DisableRegistration, logs);
                }

                eventAggregator.Publish(new UserRegisteredEvent
                {
                    RequestUri = Request.GetAbsoluteRootUri(),
                    User = user,
                    Admin = isFirstAdmin
                });

                SignInResult? signInResult = null;
                var requiresApproval = policies.RequiresUserApproval && !user.Approved;
                var requiresConfirmedEmail = policies.RequiresConfirmedEmail && !user.EmailConfirmed;
                if (!requiresConfirmedEmail && !requiresApproval)
                {
                    signInManager.AuthenticationScheme = Scheme;
                    signInResult = await signInManager.PasswordSignInAsync(signup.Email, signup.Password, true, true);
                }
                
                if (signInResult?.Succeeded is true)
                {
                    _logger.LogInformation("User {Email} logged in", user.Email);
                    return TypedResults.Empty;
                }
                var response = new ApplicationUserData
                {
                    Email = user.Email,
                    RequiresApproval = requiresApproval,
                    RequiresEmailConfirmation = requiresConfirmedEmail
                };
                return TypedResults.Ok(response);
            }
            errorMessage = result.ToString();
        }
        
        return TypedResults.Problem(errorMessage, statusCode: 401);
    }
    
    [AllowAnonymous]
    [HttpPost("login")]
    [RateLimitsFilter(ZoneLimits.Login, Scope = RateLimitsScope.RemoteAddress)]
    public async Task<Results<Ok<AccessTokenResponse>, EmptyHttpResult, ProblemHttpResult>> Login(LoginRequest login)
    {
        var errorMessage = "Invalid login attempt.";
        if (ModelState.IsValid)
        {
            // Require the user to pass basic checks (approval, confirmed email, not disabled) before they can log on
            var user = await userManager.FindByEmailAsync(login.Email);
            if (!UserService.TryCanLogin(user, out var message))
            {
                return TypedResults.Problem(message, statusCode: 401);
            }

            signInManager.AuthenticationScheme = Scheme;
            var signInResult = await signInManager.PasswordSignInAsync(login.Email, login.Password, true, true);
            if (signInResult.RequiresTwoFactor)
            {
                if (!string.IsNullOrEmpty(login.TwoFactorCode))
                    signInResult = await signInManager.TwoFactorAuthenticatorSignInAsync(login.TwoFactorCode, true, true);
                else if (!string.IsNullOrEmpty(login.TwoFactorRecoveryCode))
                    signInResult = await signInManager.TwoFactorRecoveryCodeSignInAsync(login.TwoFactorRecoveryCode);
            }
            
            // TODO: Add FIDO and LNURL Auth
            
            if (signInResult.IsLockedOut)
            {
                _logger.LogWarning("User {Email} tried to log in, but is locked out", user.Email);
            }
            else if (signInResult.Succeeded)
            {
                _logger.LogInformation("User {Email} logged in", user.Email);
                return TypedResults.Empty;
            }

            errorMessage = signInResult.ToString();
        }

        return TypedResults.Problem(errorMessage, statusCode: 401);
    }
    
    [AllowAnonymous]
    [HttpPost("login/code")]
    [RateLimitsFilter(ZoneLimits.Login, Scope = RateLimitsScope.RemoteAddress)]
    public async Task<Results<Ok<AccessTokenResponse>, EmptyHttpResult, ProblemHttpResult>> LoginWithCode([FromBody] string loginCode)
    {
        const string errorMessage = "Invalid login attempt.";
        if (!string.IsNullOrEmpty(loginCode))
        {
            var code = loginCode.Split(';').First();
            var userId = userLoginCodeService.Verify(code);
            var user = userId is null ? null : await userManager.FindByIdAsync(userId);
            if (!UserService.TryCanLogin(user, out var message))
            {
                return TypedResults.Problem(message, statusCode: 401);
            }

            signInManager.AuthenticationScheme = Scheme;
            await signInManager.SignInAsync(user, false, "LoginCode");

            _logger.LogInformation("User {Email} logged in with a login code", user.Email);
            return TypedResults.Empty;
        }

        return TypedResults.Problem(errorMessage, statusCode: 401);
    }

    [AllowAnonymous]
    [HttpPost("refresh")]
    [RateLimitsFilter(ZoneLimits.Login, Scope = RateLimitsScope.RemoteAddress)]
    public async Task<Results<Ok<AccessTokenResponse>, UnauthorizedHttpResult, SignInHttpResult, ChallengeHttpResult>> Refresh(RefreshRequest refresh)
    {
        var authenticationTicket = bearerTokenOptions.Get(Scheme).RefreshTokenProtector.Unprotect(refresh.RefreshToken);
        var expiresUtc = authenticationTicket?.Properties.ExpiresUtc;

        ApplicationUser? user = null;
        int num;
        if (expiresUtc.HasValue)
        {
            DateTimeOffset valueOrDefault = expiresUtc.GetValueOrDefault();
            num = timeProvider.GetUtcNow() >= valueOrDefault ? 1 : 0;
        }
        else
            num = 1;
        bool flag = num != 0;
        if (!flag)
        {
            signInManager.AuthenticationScheme = Scheme;
            user = await signInManager.ValidateSecurityStampAsync(authenticationTicket?.Principal);
        }
        
        return user != null
            ? TypedResults.SignIn(await signInManager.CreateUserPrincipalAsync(user), authenticationScheme: Scheme)
            : TypedResults.Challenge(authenticationSchemes: new[] { Scheme });
    }
    
    [AllowAnonymous]
    [HttpPost("accept-invite")]
    [RateLimitsFilter(ZoneLimits.Login, Scope = RateLimitsScope.RemoteAddress)]
    public async Task<IActionResult> AcceptInvite(AcceptInviteRequest invite)
    {
        if (string.IsNullOrEmpty(invite.UserId) || string.IsNullOrEmpty(invite.Code))
        {
            return NotFound();
        }

        var user = await userManager.FindByInvitationTokenAsync<ApplicationUser>(invite.UserId, Uri.UnescapeDataString(invite.Code));
        if (user == null)
        {
            return NotFound();
        }
            
        var requiresEmailConfirmation = user is { RequiresEmailConfirmation: true, EmailConfirmed: false };
        var requiresUserApproval = user is { RequiresApproval: true, Approved: false };
        bool? emailHasBeenConfirmed = requiresEmailConfirmation ? false : null;
        var requiresSetPassword = !await userManager.HasPasswordAsync(user);
        string? passwordSetCode = requiresSetPassword ? await userManager.GeneratePasswordResetTokenAsync(user) : null;
            
        eventAggregator.Publish(new UserInviteAcceptedEvent
        {
            User = user,
            RequestUri = Request.GetAbsoluteRootUri()
        });
            
        if (requiresEmailConfirmation)
        {
            var emailConfirmCode = await userManager.GenerateEmailConfirmationTokenAsync(user);
            var result = await userManager.ConfirmEmailAsync(user, emailConfirmCode);
            if (result.Succeeded)
            {
                emailHasBeenConfirmed = true;
                eventAggregator.Publish(new UserConfirmedEmailEvent
                {
                    User = user,
                    RequestUri = Request.GetAbsoluteRootUri()
                });
            }
        }

        var response = new AcceptInviteResult
        {
            Email = user.Email!,
            EmailHasBeenConfirmed = emailHasBeenConfirmed,
            RequiresUserApproval = requiresUserApproval,
            PasswordSetCode = passwordSetCode
        };
        return Ok(response);
    }

    [HttpPost("logout")]
    public async Task<IResult> Logout()
    {
        var user = await userManager.GetUserAsync(User);
        if (user != null)
        {
            await signInManager.SignOutAsync();
            _logger.LogInformation("User {Email} logged out", user.Email);
            return Results.Ok();
        }
        return Results.Unauthorized();
    }

    [HttpGet("user")]
    public async Task<IActionResult> UserInfo()
    {
        var user = await userManager.GetUserAsync(User);
        if (user == null) return NotFound();

        var userStores = await storeRepository.GetStoresByUserId(user.Id);
        var stores = new List<AppUserStoreInfo>();
        foreach (var store in userStores)
        {
            var userStore = store.UserStores.Find(us => us.ApplicationUserId == user.Id && us.StoreDataId == store.Id)!;
            var apps = await appService.GetAllApps(user.Id, false, store.Id);
            var posApp = apps.FirstOrDefault(app => app.AppType == PointOfSaleAppType.AppType && app.App.GetSettings<PointOfSaleSettings>().DefaultView == PosViewType.Light);
            var storeBlob = userStore.StoreData.GetStoreBlob();
            stores.Add(new AppUserStoreInfo
            {
                Id = store.Id,
                Name = store.StoreName,
                Archived = store.Archived,
                RoleId = userStore.StoreRole.Id,
                PosAppId = posApp?.Id,
                DefaultCurrency = storeBlob.DefaultCurrency,
                Permissions = userStore.StoreRole.Permissions,
                LogoUrl = storeBlob.LogoUrl != null
                    ? await uriResolver.Resolve(Request.GetAbsoluteRootUri(), storeBlob.LogoUrl)
                    : null,
            });
        }

        var userBlob = user.GetBlob();
        var info = new AppUserInfo
        {
            UserId = user.Id,
            Name = userBlob?.Name,
            ImageUrl = !string.IsNullOrEmpty(userBlob?.ImageUrl)
                ? await uriResolver.Resolve(Request.GetAbsoluteRootUri(), UnresolvedUri.Create(userBlob.ImageUrl))
                : null,
            Email = await userManager.GetEmailAsync(user),
            Roles = await userManager.GetRolesAsync(user),
            Stores = stores
        };
        return Ok(info);
    }

    [AllowAnonymous]
    [HttpPost("forgot-password")]
    [RateLimitsFilter(ZoneLimits.ForgotPassword, Scope = RateLimitsScope.RemoteAddress)]
    public async Task<IResult> ForgotPassword(ResetPasswordRequest resetRequest)
    {
        var user = await userManager.FindByEmailAsync(resetRequest.Email);
        if (UserService.TryCanLogin(user, out _))
        {
            eventAggregator.Publish(new UserPasswordResetRequestedEvent
            {
                User = user,
                RequestUri = Request.GetAbsoluteRootUri()
            });
        }
        return TypedResults.Ok();
    }

    [AllowAnonymous]
    [HttpPost("reset-password")]
    public async Task<Results<Ok<AccessTokenResponse>, Ok, UnauthorizedHttpResult, EmptyHttpResult, ProblemHttpResult>> SetPassword(ResetPasswordRequest resetRequest)
    {
        var user = await userManager.FindByEmailAsync(resetRequest.Email);
        var needsInitialPassword = user != null && !await userManager.HasPasswordAsync(user);
        // Let unapproved users set a password. Otherwise, don't reveal that the user does not exist.
        if (!UserService.TryCanLogin(user, out var message) && !needsInitialPassword || user == null)
        {
            _logger.LogWarning("User {Email} tried to reset password, but failed: {Message}", user?.Email ?? "(NO EMAIL)", message);
            return TypedResults.Problem("Invalid request", statusCode: 401);
        }

        IdentityResult result;
        try
        {
            result = await userManager.ResetPasswordAsync(user, resetRequest.ResetCode, resetRequest.NewPassword);
        }
        catch (FormatException)
        {
            result = IdentityResult.Failed(userManager.ErrorDescriber.InvalidToken());
        }
        
        if (!result.Succeeded) return TypedResults.Problem(result.ToString().Split(": ").Last(), statusCode: 401);
        
        // see if we can sign in user after accepting an invitation and setting the password 
        if (needsInitialPassword && UserService.TryCanLogin(user, out _))
        {
            signInManager.AuthenticationScheme = Scheme;
            var signInResult = await signInManager.PasswordSignInAsync(user.Email!, resetRequest.NewPassword, true, true);
            if (signInResult.Succeeded)
            {
                _logger.LogInformation("User {Email} logged in", user.Email);
                return TypedResults.Empty;
            }
        }
        
        return TypedResults.Ok();
    }
}
