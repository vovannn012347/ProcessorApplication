using System.Security.Principal;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using P2PModule.Interfaces;
using P2PModule.Models;
using P2PModule.Services.Connection;
using P2PModule.Services.Persistence;

namespace P2PModule.Infrastructure.Endpoints;

public static class ConnectionEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/p2p/ping", async context =>
        {
            // in case i would want some cryptographic confirmation here
            var internalKey = context.Request.Query["definedKey"].ToString();
            //do soemthing with key?
            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.WriteAsync("PONG");
        }).AllowAnonymous();

        // Discovery Endpoint: Resolves Node identity parameters securely for bootstrapping processes
        endpoints.MapGet("/p2p/identity", async context =>
        {
            var identityService = context.RequestServices.GetRequiredService<INodeIdentityService>();
            var logger = context.RequestServices.GetRequiredService<ILogger<IPeerConnectionManager>>();

            try
            {
                var identityData = identityService.GetImmutableData();
                //
                //{
                //    NodeId = identityService.NodeId,
                //    SignalingEndpoints = identityService.GetEndpoints(),
                //    Roles = identityService.GetCapacities(),
                //    PublicEncryptionKey = identityService.PublicKey,
                //    PublicVerifyKey = identityService.PublicVerifyKey,
                //    SignatureTime = DateTime.UtcNow
                //};
                //identityData.Signature = P2PCrypto.SignData(identityData.GetNodeDataHash(), identityService.PrivateSignatureKey);

                context.Response.StatusCode = StatusCodes.Status200OK;
                await context.Response.WriteAsJsonAsync(identityData);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[P2P Endpoints] Failed to retrieve local node identity.");
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            }
        }).AllowAnonymous();

        // HANDSHAKE STEP 1 & 2: Process the Handshake Challenge request and respond with AES Decrypted challenge calculations
        endpoints.MapPost("/p2p/handshake/challenge", async context =>
        {
            var challengeService = context.RequestServices.GetRequiredService<IChallengeService>();
            try
            {
                var response = await challengeService.HandleHandshakeMessage(context.Request);
                if (!string.IsNullOrEmpty(response))
                {
                    await context.Response.WriteAsync(response);
                    //context.Response.StatusCode = StatusCodes.Status200OK;
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status400BadRequest;
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            }
        })
        .AllowAnonymous();

        // signalling endpoint
        endpoints.MapPost("/p2p/webrtc/signal", async context =>
        {
            var connectionManager = context.RequestServices.GetRequiredService<IPeerConnectionManager>();
            var logger = context.RequestServices.GetRequiredService<ILogger<P2PModuleEntryPoint>>();

            try
            {
                var signal = await context.Request.ReadFromJsonAsync<WebRTCSignalPayload>();
                if (signal != null && signal.Type == "offer")
                {
                    var answerSdp = await connectionManager.HandleIncomingSignalingHttpAsync(signal.SenderNodeId, signal.Sdp, signal.HandShakeId);
                    if (!string.IsNullOrEmpty(answerSdp))
                    {
                        await context.Response.WriteAsJsonAsync(new WebRTCSignalPayload { SenderNodeId = signal.SenderNodeId, Sdp = answerSdp, Type = "answer" });
                        return;
                    }
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                }
                else context.Response.StatusCode = StatusCodes.Status400BadRequest;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[P2P Endpoints] Direct WebRTC signaling handover failed.");
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            }
        })
        .AllowAnonymous();
    }
}