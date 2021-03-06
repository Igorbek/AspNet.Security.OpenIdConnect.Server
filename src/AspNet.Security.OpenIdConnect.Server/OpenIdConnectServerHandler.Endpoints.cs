/*
 * Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)
 * See https://github.com/aspnet-contrib/AspNet.Security.OpenIdConnect.Server
 * for more information concerning the license and the contributors participating to this project.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using AspNet.Security.OpenIdConnect.Extensions;
using Microsoft.AspNet.Authentication;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.Net.Http.Headers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AspNet.Security.OpenIdConnect.Server {
    internal partial class OpenIdConnectServerHandler : AuthenticationHandler<OpenIdConnectServerOptions> {
        private async Task<bool> InvokeAuthorizationEndpointAsync() {
            OpenIdConnectMessage request;

            if (string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase)) {
                // Create a new authorization request using the
                // parameters retrieved from the query string.
                request = new OpenIdConnectMessage(Request.Query.ToDictionary()) {
                    RequestType = OpenIdConnectRequestType.AuthenticationRequest
                };
            }

            else if (string.Equals(Request.Method, "POST", StringComparison.OrdinalIgnoreCase)) {
                // See http://openid.net/specs/openid-connect-core-1_0.html#FormSerialization
                if (string.IsNullOrEmpty(Request.ContentType)) {
                    Logger.LogInformation("A malformed request has been received by the authorization endpoint.");

                    return await SendErrorPageAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed authorization request has been received: " +
                            "the mandatory 'Content-Type' header was missing from the POST request."
                    });
                }

                // May have media/type; charset=utf-8, allow partial match.
                if (!Request.ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)) {
                    Logger.LogInformation("A malformed request has been received by the authorization endpoint.");

                    return await SendErrorPageAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed authorization request has been received: " +
                            "the 'Content-Type' header contained an unexcepted value. " +
                            "Make sure to use 'application/x-www-form-urlencoded'."
                    });
                }

                // Create a new authorization request using the
                // parameters retrieved from the request form.
                var form = await Request.ReadFormAsync(Context.RequestAborted);

                request = new OpenIdConnectMessage(form.ToDictionary()) {
                    RequestType = OpenIdConnectRequestType.AuthenticationRequest
                };
            }

            else {
                Logger.LogInformation("A malformed request has been received by the authorization endpoint.");

                return await SendErrorPageAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed authorization request has been received: " +
                        "make sure to use either GET or POST."
                });
            }

            // Re-assemble the authorization request using the distributed cache if
            // a 'unique_id' parameter has been extracted from the received message.
            var identifier = request.GetUniqueIdentifier();
            if (!string.IsNullOrEmpty(identifier)) {
                var buffer = await Options.Cache.GetAsync(identifier);
                if (buffer == null) {
                    Logger.LogInformation("A unique_id has been provided but no corresponding " +
                                          "OpenID Connect request has been found in the distributed cache.");

                    return await SendErrorPageAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "Invalid request: timeout expired."
                    });
                }

                using (var stream = new MemoryStream(buffer))
                using (var reader = new BinaryReader(stream)) {
                    // Make sure the stored authorization request
                    // has been serialized using the same method.
                    var version = reader.ReadInt32();
                    if (version != 1) {
                        await Options.Cache.RemoveAsync(identifier);

                        Logger.LogError("An invalid OpenID Connect request has been found in the distributed cache.");

                        return await SendErrorPageAsync(new OpenIdConnectMessage {
                            Error = OpenIdConnectConstants.Errors.InvalidRequest,
                            ErrorDescription = "Invalid request: timeout expired."
                        });
                    }
                    
                    for (int index = 0, length = reader.ReadInt32(); index < length; index++) {
                        var name = reader.ReadString();
                        var value = reader.ReadString();

                        // Skip restoring the parameter retrieved from the stored request
                        // if the OpenID Connect message extracted from the query string
                        // or the request form defined the same parameter.
                        if (!request.Parameters.ContainsKey(name)) {
                            request.SetParameter(name, value);
                        }
                    }
                }
            }

            // Store the authorization request in the ASP.NET context.
            Context.SetOpenIdConnectRequest(request);

            // client_id is mandatory parameter and MUST cause an error when missing.
            // See http://openid.net/specs/openid-connect-core-1_0.html#AuthRequest
            if (string.IsNullOrEmpty(request.ClientId)) {
                return await SendErrorPageAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "client_id was missing"
                });
            }

            // While redirect_uri was not mandatory in OAuth2, this parameter
            // is now declared as REQUIRED and MUST cause an error when missing.
            // See http://openid.net/specs/openid-connect-core-1_0.html#AuthRequest
            // To keep AspNet.Security.OpenIdConnect.Server compatible with pure OAuth2 clients,
            // an error is only returned if the request was made by an OpenID Connect client.
            if (string.IsNullOrEmpty(request.RedirectUri) && request.ContainsScope(OpenIdConnectConstants.Scopes.OpenId)) {
                return await SendErrorPageAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "redirect_uri must be included when making an OpenID Connect request"
                });
            }

            if (!string.IsNullOrEmpty(request.RedirectUri)) {
                Uri uri;
                if (!Uri.TryCreate(request.RedirectUri, UriKind.Absolute, out uri)) {
                    // redirect_uri MUST be an absolute URI.
                    // See http://tools.ietf.org/html/rfc6749#section-3.1.2
                    // and http://openid.net/specs/openid-connect-core-1_0.html#AuthRequest
                    return await SendErrorPageAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "redirect_uri must be absolute"
                    });
                }

                else if (!string.IsNullOrEmpty(uri.Fragment)) {
                    // redirect_uri MUST NOT include a fragment component.
                    // See http://tools.ietf.org/html/rfc6749#section-3.1.2
                    // and http://openid.net/specs/openid-connect-core-1_0.html#AuthRequest
                    return await SendErrorPageAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "redirect_uri must not include a fragment"
                    });
                }

                else if (!Options.AllowInsecureHttp && string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase)) {
                    // redirect_uri SHOULD require the use of TLS
                    // http://tools.ietf.org/html/rfc6749#section-3.1.2.1
                    // and http://openid.net/specs/openid-connect-core-1_0.html#AuthRequest
                    return await SendErrorPageAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "redirect_uri does not meet the security requirements"
                    });
                }
            }

            var clientNotification = new ValidateClientRedirectUriContext(Context, Options, request);
            await Options.Provider.ValidateClientRedirectUri(clientNotification);

            // Reject the authorization request if the redirect_uri was not validated.
            if (!clientNotification.IsValidated) {
                Logger.LogVerbose("Unable to validate client information");

                return await SendErrorPageAsync(new OpenIdConnectMessage {
                    Error = clientNotification.Error ?? OpenIdConnectConstants.Errors.InvalidClient,
                    ErrorDescription = clientNotification.ErrorDescription,
                    ErrorUri = clientNotification.ErrorUri
                });
            }

            if (string.IsNullOrEmpty(request.ResponseType)) {
                Logger.LogVerbose("Authorization request missing required response_type parameter");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "response_type parameter missing",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            else if (!request.IsNoneFlow() && !request.IsAuthorizationCodeFlow() &&
                     !request.IsImplicitFlow() && !request.IsHybridFlow()) {
                Logger.LogVerbose("Authorization request contains unsupported response_type parameter");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.UnsupportedResponseType,
                    ErrorDescription = "response_type unsupported",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            else if (!request.IsFormPostResponseMode() && !request.IsFragmentResponseMode() && !request.IsQueryResponseMode()) {
                Logger.LogVerbose("Authorization request contains unsupported response_mode parameter");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "response_mode unsupported",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            // response_mode=query (explicit or not) and a response_type containing id_token
            // or token are not considered as a safe combination and MUST be rejected.
            // See http://openid.net/specs/oauth-v2-multiple-response-types-1_0.html#Security
            else if (request.IsQueryResponseMode() && (request.ContainsResponseType(OpenIdConnectConstants.ResponseTypes.IdToken) ||
                                                       request.ContainsResponseType(OpenIdConnectConstants.ResponseTypes.Token))) {
                Logger.LogVerbose("Authorization request contains unsafe response_type/response_mode combination");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "response_type/response_mode combination unsupported",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            // Reject OpenID Connect implicit/hybrid requests missing the mandatory nonce parameter.
            // See http://openid.net/specs/openid-connect-core-1_0.html#AuthRequest,
            // http://openid.net/specs/openid-connect-implicit-1_0.html#RequestParameters
            // and http://openid.net/specs/openid-connect-core-1_0.html#HybridIDToken.
            else if (string.IsNullOrEmpty(request.Nonce) && request.ContainsScope(OpenIdConnectConstants.Scopes.OpenId) &&
                                                           (request.IsImplicitFlow() || request.IsHybridFlow())) {
                Logger.LogVerbose("The 'nonce' parameter was missing");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "nonce parameter missing",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }
            
            // Reject requests containing the id_token response_mode if no openid scope has been received.
            else if (request.ContainsResponseType(OpenIdConnectConstants.ResponseTypes.IdToken) &&
                    !request.ContainsScope(OpenIdConnectConstants.Scopes.OpenId)) {
                Logger.LogVerbose("The 'openid' scope part was missing");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "openid scope missing",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            // Reject requests containing the code response_mode if the token endpoint has been disabled.
            else if (request.ContainsResponseType(OpenIdConnectConstants.ResponseTypes.Code) &&
                    !Options.TokenEndpointPath.HasValue) {
                Logger.LogVerbose("Authorization request contains the disabled code response_type");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.UnsupportedResponseType,
                    ErrorDescription = "response_type=code is not supported by this server",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            // Reject requests containing the id_token response_mode if no signing credentials have been provided.
            else if (request.ContainsResponseType(OpenIdConnectConstants.ResponseTypes.IdToken) &&
                     Options.SigningCredentials == null) {
                Logger.LogVerbose("Authorization request contains the disabled id_token response_type");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.UnsupportedResponseType,
                    ErrorDescription = "response_type=id_token is not supported by this server",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            var validationNotification = new ValidateAuthorizationRequestContext(Context, Options, request, clientNotification);
            await Options.Provider.ValidateAuthorizationRequest(validationNotification);

            // Stop processing the request if Validated was not called.
            if (!validationNotification.IsValidated) {
                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage {
                    Error = validationNotification.Error ?? OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = validationNotification.ErrorDescription,
                    ErrorUri = validationNotification.ErrorUri,
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            identifier = request.GetUniqueIdentifier();
            if (string.IsNullOrEmpty(identifier)) {
                // Generate a new 256-bits identifier and associate it with the authorization request.
                identifier = GenerateKey(length: 256 / 8);
                request.SetUniqueIdentifier(identifier);

                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream)) {
                    writer.Write(/* version: */ 1);
                    writer.Write(request.Parameters.Count);

                    foreach (var parameter in request.Parameters) {
                        writer.Write(parameter.Key);
                        writer.Write(parameter.Value);
                    }

                    // Store the authorization request in the distributed cache.
                    await Options.Cache.SetAsync(request.GetUniqueIdentifier(), options => {
                        options.SetAbsoluteExpiration(TimeSpan.FromHours(1));

                        return stream.ToArray();
                    });
                }
            }

            var notification = new AuthorizationEndpointContext(Context, Options, request);
            await Options.Provider.AuthorizationEndpoint(notification);

            if (notification.HandledResponse) {
                return true;
            }

            return false;
        }

        private async Task InvokeConfigurationEndpointAsync() {
            var notification = new ConfigurationEndpointContext(Context, Options);
            notification.Issuer = Context.GetIssuer(Options);

            // Metadata requests must be made via GET.
            // See http://openid.net/specs/openid-connect-discovery-1_0.html#ProviderConfigurationRequest
            if (!string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase)) {
                Logger.LogError(string.Format(CultureInfo.InvariantCulture,
                    "Configuration endpoint: invalid method '{0}' used", Request.Method));
                return;
            }

            if (Options.AuthorizationEndpointPath.HasValue) {
                notification.AuthorizationEndpoint = notification.Issuer.AddPath(Options.AuthorizationEndpointPath);
            }

            if (Options.CryptographyEndpointPath.HasValue) {
                notification.CryptographyEndpoint = notification.Issuer.AddPath(Options.CryptographyEndpointPath);
            }

            if (Options.ProfileEndpointPath.HasValue) {
                notification.ProfileEndpoint = notification.Issuer.AddPath(Options.ProfileEndpointPath);
            }

            if (Options.TokenEndpointPath.HasValue) {
                notification.TokenEndpoint = notification.Issuer.AddPath(Options.TokenEndpointPath);
            }

            if (Options.LogoutEndpointPath.HasValue) {
                notification.LogoutEndpoint = notification.Issuer.AddPath(Options.LogoutEndpointPath);
            }

            if (Options.AuthorizationEndpointPath.HasValue) {
                // Only expose the implicit grant type if the token
                // endpoint has not been explicitly disabled.
                notification.GrantTypes.Add(OpenIdConnectConstants.GrantTypes.Implicit);

                if (Options.TokenEndpointPath.HasValue) {
                    // Only expose the authorization code and refresh token grant types
                    // if both the authorization and the token endpoints are enabled.
                    notification.GrantTypes.Add(OpenIdConnectConstants.GrantTypes.AuthorizationCode);
                }
            }

            if (Options.TokenEndpointPath.HasValue) {
                notification.GrantTypes.Add(OpenIdConnectConstants.GrantTypes.RefreshToken);

                // If the authorization endpoint is disabled, assume the authorization server will
                // allow the client credentials and resource owner password credentials grant types.
                if (!Options.AuthorizationEndpointPath.HasValue) {
                    notification.GrantTypes.Add(OpenIdConnectConstants.GrantTypes.ClientCredentials);
                    notification.GrantTypes.Add(OpenIdConnectConstants.GrantTypes.Password);
                }
            }

            // Only populate response_modes_supported and response_types_supported
            // if the authorization endpoint is available.
            if (Options.AuthorizationEndpointPath.HasValue) {
                notification.ResponseModes.Add(OpenIdConnectConstants.ResponseModes.FormPost);
                notification.ResponseModes.Add(OpenIdConnectConstants.ResponseModes.Fragment);
                notification.ResponseModes.Add(OpenIdConnectConstants.ResponseModes.Query);

                notification.ResponseTypes.Add(OpenIdConnectConstants.ResponseTypes.Token);

                // Only expose response types containing id_token when
                // signing credentials have been explicitly provided.
                if (Options.SigningCredentials != null) {
                    notification.ResponseTypes.Add(OpenIdConnectConstants.ResponseTypes.IdToken);
                    notification.ResponseTypes.Add(
                        OpenIdConnectConstants.ResponseTypes.IdToken + ' ' +
                        OpenIdConnectConstants.ResponseTypes.Token);
                }

                // Only expose response types containing code when
                // the token endpoint has not been explicitly disabled.
                if (Options.TokenEndpointPath.HasValue) {
                    notification.ResponseTypes.Add(OpenIdConnectConstants.ResponseTypes.Code);

                    notification.ResponseTypes.Add(
                        OpenIdConnectConstants.ResponseTypes.Code + ' ' +
                        OpenIdConnectConstants.ResponseTypes.Token);

                    // Only expose response types containing id_token when
                    // signing credentials have been explicitly provided.
                    if (Options.SigningCredentials != null) {
                        notification.ResponseTypes.Add(
                            OpenIdConnectConstants.ResponseTypes.Code + ' ' +
                            OpenIdConnectConstants.ResponseTypes.IdToken);

                        notification.ResponseTypes.Add(
                            OpenIdConnectConstants.ResponseTypes.Code + ' ' +
                            OpenIdConnectConstants.ResponseTypes.IdToken + ' ' +
                            OpenIdConnectConstants.ResponseTypes.Token);
                    }
                }
            }

            notification.Scopes.Add(OpenIdConnectConstants.Scopes.OpenId);

            notification.SubjectTypes.Add(OpenIdConnectConstants.SubjectTypes.Public);

            notification.SigningAlgorithms.Add(OpenIdConnectConstants.Algorithms.RS256);

            await Options.Provider.ConfigurationEndpoint(notification);

            if (notification.HandledResponse) {
                return;
            }

            var payload = new JObject();

            payload.Add(OpenIdConnectConstants.Metadata.Issuer, notification.Issuer);

            if (!string.IsNullOrEmpty(notification.AuthorizationEndpoint)) {
                payload.Add(OpenIdConnectConstants.Metadata.AuthorizationEndpoint, notification.AuthorizationEndpoint);
            }

            if (!string.IsNullOrEmpty(notification.ProfileEndpoint)) {
                payload.Add(OpenIdConnectConstants.Metadata.UserinfoEndpoint, notification.ProfileEndpoint);
            }

            if (!string.IsNullOrEmpty(notification.TokenEndpoint)) {
                payload.Add(OpenIdConnectConstants.Metadata.TokenEndpoint, notification.TokenEndpoint);
            }

            if (!string.IsNullOrEmpty(notification.LogoutEndpoint)) {
                payload.Add(OpenIdConnectConstants.Metadata.EndSessionEndpoint, notification.LogoutEndpoint);
            }

            if (!string.IsNullOrEmpty(notification.CryptographyEndpoint)) {
                payload.Add(OpenIdConnectConstants.Metadata.JwksUri, notification.CryptographyEndpoint);
            }

            payload.Add(OpenIdConnectConstants.Metadata.GrantTypesSupported,
                JArray.FromObject(notification.GrantTypes));

            payload.Add(OpenIdConnectConstants.Metadata.ResponseModesSupported,
                JArray.FromObject(notification.ResponseModes));

            payload.Add(OpenIdConnectConstants.Metadata.ResponseTypesSupported,
                JArray.FromObject(notification.ResponseTypes));

            payload.Add(OpenIdConnectConstants.Metadata.SubjectTypesSupported,
                JArray.FromObject(notification.SubjectTypes));

            payload.Add(OpenIdConnectConstants.Metadata.ScopesSupported,
                JArray.FromObject(notification.Scopes));

            payload.Add(OpenIdConnectConstants.Metadata.IdTokenSigningAlgValuesSupported,
                JArray.FromObject(notification.SigningAlgorithms));

            var context = new ConfigurationEndpointResponseContext(Context, Options, payload);
            await Options.Provider.ConfigurationEndpointResponse(context);

            if (context.HandledResponse) {
                return;
            }

            using (var buffer = new MemoryStream())
            using (var writer = new JsonTextWriter(new StreamWriter(buffer))) {
                payload.WriteTo(writer);
                writer.Flush();

                Response.ContentLength = buffer.Length;
                Response.ContentType = "application/json;charset=UTF-8";

                buffer.Seek(offset: 0, loc: SeekOrigin.Begin);
                await buffer.CopyToAsync(Response.Body, 4096, Context.RequestAborted);
            }
        }

        private async Task InvokeCryptographyEndpointAsync() {
            var notification = new CryptographyEndpointContext(Context, Options);

            // Metadata requests must be made via GET.
            // See http://openid.net/specs/openid-connect-discovery-1_0.html#ProviderConfigurationRequest
            if (!string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase)) {
                Logger.LogError(string.Format(CultureInfo.InvariantCulture,
                    "Cryptography endpoint: invalid method '{0}' used", Request.Method));
                return;
            }

            foreach (var credentials in Options.SigningCredentials) {
                if (!credentials.SigningKey.IsSupportedAlgorithm(SecurityAlgorithms.RsaSha256Signature)) {
                    Logger.LogWarning(string.Format(CultureInfo.InvariantCulture,
                        "Cryptography endpoint: invalid signing key registered. " +
                        "Make sure to provide a '{0}' instance exposing " +
                        "an asymmetric security key supporting the '{1}' algorithm.",
                        typeof(SigningCredentials).Name, SecurityAlgorithms.RsaSha256Signature));

                    continue;
                }

                // Determine whether the security key is a RSA key embedded in a X.509 certificate.
                var x509SecurityKey = credentials.SigningKey as X509SecurityKey;
                if (x509SecurityKey != null) {
                    // Create a new JSON Web Key exposing the
                    // certificate instead of its public RSA key.
                    notification.Keys.Add(new JsonWebKey {
                        Kty = JsonWebAlgorithmsKeyTypes.RSA,
                        Alg = JwtAlgorithms.RSA_SHA256,
                        Use = JsonWebKeyUseNames.Sig,

                        // By default, use the key identifier specified in the signing credentials. If none is specified,
                        // use the hexadecimal representation of the certificate's SHA-1 hash as the unique key identifier.
                        Kid = credentials.Kid ?? x509SecurityKey.KeyId ?? x509SecurityKey.Certificate.Thumbprint,

                        // x5t must be base64url-encoded.
                        // See http://tools.ietf.org/html/draft-ietf-jose-json-web-key-31#section-4.8
                        X5t = Base64UrlEncoder.Encode(x509SecurityKey.Certificate.GetCertHash()),

                        // Unlike E or N, the certificates contained in x5c
                        // must be base64-encoded and not base64url-encoded.
                        // See http://tools.ietf.org/html/draft-ietf-jose-json-web-key-31#section-4.7
                        X5c = { Convert.ToBase64String(x509SecurityKey.Certificate.RawData) }
                    });
                }

                var rsaSecurityKey = credentials.SigningKey as RsaSecurityKey;
                if (rsaSecurityKey != null) {
                    // Export the RSA public key.
                    notification.Keys.Add(new JsonWebKey {
                        Kty = JsonWebAlgorithmsKeyTypes.RSA,
                        Alg = JwtAlgorithms.RSA_SHA256,
                        Use = JsonWebKeyUseNames.Sig,

                        // By default, use the key identifier specified in the signing credentials.
                        // If none is specified, try to extract it from the security key itself or
                        // create one using the base64url-encoded representation of the modulus.
                        Kid = credentials.Kid ?? rsaSecurityKey.KeyId ??
                            // Note: use the first 40 chars to avoid using a too long identifier.
                            Base64UrlEncoder.Encode(rsaSecurityKey.Parameters.Modulus)
                                            .Substring(0, 40).ToUpperInvariant(),

                        // Both E and N must be base64url-encoded.
                        // See http://tools.ietf.org/html/draft-ietf-jose-json-web-key-31#appendix-A.1
                        E = Base64UrlEncoder.Encode(rsaSecurityKey.Parameters.Exponent),
                        N = Base64UrlEncoder.Encode(rsaSecurityKey.Parameters.Modulus)
                    });
                }
            }

            await Options.Provider.CryptographyEndpoint(notification);

            if (notification.HandledResponse) {
                return;
            }

            var payload = new JObject();
            var keys = new JArray();

            foreach (var key in notification.Keys) {
                var item = new JObject();

                // Ensure a key type has been provided.
                // See http://tools.ietf.org/html/draft-ietf-jose-json-web-key-31#section-4.1
                if (string.IsNullOrEmpty(key.Kty)) {
                    Logger.LogWarning("Cryptography endpoint: a JSON Web Key didn't " +
                        "contain the mandatory 'Kty' parameter and has been ignored.");

                    continue;
                }

                // Create a dictionary associating the
                // JsonWebKey components with their values.
                var parameters = new Dictionary<string, string> {
                    [JsonWebKeyParameterNames.Kid] = key.Kid,
                    [JsonWebKeyParameterNames.Use] = key.Use,
                    [JsonWebKeyParameterNames.Kty] = key.Kty,
                    [JsonWebKeyParameterNames.Alg] = key.Alg,
                    [JsonWebKeyParameterNames.X5t] = key.X5t,
                    [JsonWebKeyParameterNames.X5u] = key.X5u,
                    [JsonWebKeyParameterNames.E] = key.E,
                    [JsonWebKeyParameterNames.N] = key.N
                };

                foreach (var parameter in parameters) {
                    if (!string.IsNullOrEmpty(parameter.Value)) {
                        item.Add(parameter.Key, parameter.Value);
                    }
                }

                if (key.KeyOps.Any()) {
                    item.Add(JsonWebKeyParameterNames.KeyOps, JArray.FromObject(key.KeyOps));
                }

                if (key.X5c.Any()) {
                    item.Add(JsonWebKeyParameterNames.X5c, JArray.FromObject(key.X5c));
                }

                keys.Add(item);
            }

            payload.Add(JsonWebKeyParameterNames.Keys, keys);

            var context = new CryptographyEndpointResponseContext(Context, Options, payload);
            await Options.Provider.CryptographyEndpointResponse(context);

            if (context.HandledResponse) {
                return;
            }

            using (var buffer = new MemoryStream())
            using (var writer = new JsonTextWriter(new StreamWriter(buffer))) {
                payload.WriteTo(writer);
                writer.Flush();

                Response.ContentLength = buffer.Length;
                Response.ContentType = "application/json;charset=UTF-8";

                buffer.Seek(offset: 0, loc: SeekOrigin.Begin);
                await buffer.CopyToAsync(Response.Body, 4096, Context.RequestAborted);
            }
        }

        private async Task InvokeTokenEndpointAsync() {
            if (!string.Equals(Request.Method, "POST", StringComparison.OrdinalIgnoreCase)) {
                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed token request has been received: make sure to use POST."
                });

                return;
            }

            // See http://openid.net/specs/openid-connect-core-1_0.html#FormSerialization
            if (string.IsNullOrEmpty(Request.ContentType)) {
                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed token request has been received: " +
                        "the mandatory 'Content-Type' header was missing from the POST request."
                });

                return;
            }

            // May have media/type; charset=utf-8, allow partial match.
            if (!Request.ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)) {
                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed token request has been received: " +
                        "the 'Content-Type' header contained an unexcepted value. " +
                        "Make sure to use 'application/x-www-form-urlencoded'."
                });

                return;
            }

            var form = await Request.ReadFormAsync(Context.RequestAborted);

            var request = new OpenIdConnectMessage(form.ToDictionary()) {
                RequestType = OpenIdConnectRequestType.TokenRequest
            };

            // Reject token requests missing the mandatory grant_type parameter.
            if (string.IsNullOrEmpty(request.GrantType)) {
                Logger.LogError("grant_type missing");

                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "The mandatory grant_type parameter is missing",
                });

                return;
            }

            // Note: client_id is mandatory when using the authorization code grant
            // and must be manually flowed by non-confidential client applications.
            // See https://tools.ietf.org/html/rfc6749#section-4.1.3
            if (request.IsAuthorizationCodeGrantType() && string.IsNullOrEmpty(request.ClientId)) {
                Logger.LogError("client_id was missing from the token request");

                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "client_id was missing from the token request"
                });

                return;
            }

            // Reject grant_type=password requests missing username or password.
            // See https://tools.ietf.org/html/rfc6749#section-4.3.2
            if (request.IsPasswordGrantType() && (string.IsNullOrEmpty(request.Username) ||
                                                  string.IsNullOrEmpty(request.Password))) {
                Logger.LogError("resource owner credentials missing.");

                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "username and/or password were missing from the request message"
                });

                return;
            }

            // When client_id and client_secret are both null, try to extract them from the Authorization header.
            // See http://tools.ietf.org/html/rfc6749#section-2.3.1 and
            // http://openid.net/specs/openid-connect-core-1_0.html#ClientAuthentication
            if (string.IsNullOrEmpty(request.ClientId) && string.IsNullOrEmpty(request.ClientSecret)) {
                string header = Request.Headers[HeaderNames.Authorization];
                if (!string.IsNullOrEmpty(header) && header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) {
                    try {
                        var value = header.Substring("Basic ".Length).Trim();
                        var data = Encoding.UTF8.GetString(Convert.FromBase64String(value));

                        var index = data.IndexOf(':');
                        if (index >= 0) {
                            request.ClientId = data.Substring(0, index);
                            request.ClientSecret = data.Substring(index + 1);
                        }
                    }

                    catch (FormatException) { }
                    catch (ArgumentException) { }
                }
            }

            var clientNotification = new ValidateClientAuthenticationContext(Context, Options, request);
            await Options.Provider.ValidateClientAuthentication(clientNotification);

            // Reject the request if client authentication was rejected.
            if (clientNotification.IsRejected) {
                Logger.LogError("invalid client authentication.");

                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = clientNotification.Error ?? OpenIdConnectConstants.Errors.InvalidClient,
                    ErrorDescription = clientNotification.ErrorDescription,
                    ErrorUri = clientNotification.ErrorUri
                });

                return;
            }

            // Reject grant_type=client_credentials requests if client authentication was skipped.
            if (clientNotification.IsSkipped && request.IsClientCredentialsGrantType()) {
                Logger.LogError("client authentication is required for client_credentials grant type.");

                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidGrant,
                    ErrorDescription = "client authentication is required when using client_credentials"
                });

                return;
            }

            var validatingContext = new ValidateTokenRequestContext(Context, Options, request, clientNotification);

            // Validate the token request immediately if the grant type used by
            // the client application doesn't rely on a previously-issued token/code.
            if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType()) {
                await Options.Provider.ValidateTokenRequest(validatingContext);

                if (!validatingContext.IsValidated) {
                    // Note: use invalid_request as the default error if none has been explicitly provided.
                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = validatingContext.Error ?? OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = validatingContext.ErrorDescription,
                        ErrorUri = validatingContext.ErrorUri
                    });

                    return;
                }
            }

            AuthenticationTicket ticket = null;

            // See http://tools.ietf.org/html/rfc6749#section-4.1
            // and http://tools.ietf.org/html/rfc6749#section-4.1.3 (authorization code grant).
            // See http://tools.ietf.org/html/rfc6749#section-6 (refresh token grant).
            if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType()) {
                ticket = request.IsAuthorizationCodeGrantType() ?
                    await ReceiveAuthorizationCodeAsync(request.Code, request) :
                    await ReceiveRefreshTokenAsync(request.RefreshToken, request);

                if (ticket == null) {
                    Logger.LogError("invalid ticket");

                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidGrant,
                        ErrorDescription = "Invalid ticket"
                    });

                    return;
                }

                if (!ticket.Properties.ExpiresUtc.HasValue ||
                     ticket.Properties.ExpiresUtc < Options.SystemClock.UtcNow) {
                    Logger.LogError("expired ticket");

                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidGrant,
                        ErrorDescription = "Expired ticket"
                    });

                    return;
                }

                // Validate the redirect_uri flowed by the client application during this token request.
                // Note: for pure OAuth2 requests, redirect_uri is only mandatory if the authorization request
                // contained an explicit redirect_uri. OpenID Connect requests MUST include a redirect_uri
                // but the specifications allow proceeding the token request without returning an error
                // if the authorization request didn't contain an explicit redirect_uri.
                // See https://tools.ietf.org/html/rfc6749#section-4.1.3
                // and http://openid.net/specs/openid-connect-core-1_0.html#TokenRequestValidation
                string address;
                if (request.IsAuthorizationCodeGrantType() &&
                    ticket.Properties.Items.TryGetValue(OpenIdConnectConstants.Extra.RedirectUri, out address)) {
                    ticket.Properties.Items.Remove(OpenIdConnectConstants.Extra.RedirectUri);

                    if (!string.Equals(address, request.RedirectUri, StringComparison.Ordinal)) {
                        Logger.LogError("authorization code does not contain matching redirect_uri");

                        await SendErrorPayloadAsync(new OpenIdConnectMessage {
                            Error = OpenIdConnectConstants.Errors.InvalidGrant,
                            ErrorDescription = "Authorization code does not contain matching redirect_uri"
                        });

                        return;
                    }
                }

                // If the client was fully authenticated when retrieving its refresh token,
                // the current request must be rejected if client authentication was not enforced.
                if (request.IsRefreshTokenGrantType() && !clientNotification.IsValidated &&
                    ticket.ContainsProperty(OpenIdConnectConstants.Extra.ClientAuthenticated)) {
                    Logger.LogError("client authentication is required to use this ticket");

                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidGrant,
                        ErrorDescription = "Client authentication is required to use this ticket"
                    });

                    return;
                }

                // Note: identifier may be null for non-confidential client applications
                // whose refresh token has been issued without requiring authentication.
                // When using the refresh token grant, client_id is optional but must validated if present.
                // See https://tools.ietf.org/html/rfc6749#section-6
                // and http://openid.net/specs/openid-connect-core-1_0.html#RefreshingAccessToken
                var identifier = ticket.Properties.GetProperty(OpenIdConnectConstants.Extra.ClientId);
                if (!string.IsNullOrEmpty(identifier) && !string.Equals(identifier, request.ClientId, StringComparison.Ordinal)) {
                    Logger.LogError("ticket does not contain matching client_id");

                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidGrant,
                        ErrorDescription = "Ticket does not contain matching client_id"
                    });

                    return;
                }

                if (!string.IsNullOrEmpty(request.Resource)) {
                    // When an explicit resource parameter has been included in the token request
                    // but was missing from the authorization request, the request MUST rejected.
                    var resources = ticket.Properties.GetResources();
                    if (!resources.Any()) {
                        Logger.LogError("token request cannot contain a resource");

                        await SendErrorPayloadAsync(new OpenIdConnectMessage {
                            Error = OpenIdConnectConstants.Errors.InvalidGrant,
                            ErrorDescription = "Token request cannot contain a resource parameter" +
                                               "if the authorization request didn't contain one"
                        });

                        return;
                    }

                    // When an explicit resource parameter has been included in the token request,
                    // the authorization server MUST ensure that it doesn't contain resources
                    // that were not allowed during the authorization request.
                    else if (!resources.ContainsSet(request.GetResources())) {
                        Logger.LogError("token request does not contain matching resource");

                        await SendErrorPayloadAsync(new OpenIdConnectMessage {
                            Error = OpenIdConnectConstants.Errors.InvalidGrant,
                            ErrorDescription = "Token request doesn't contain a valid resource parameter"
                        });

                        return;
                    }
                }

                if (!string.IsNullOrEmpty(request.Scope)) {
                    // When an explicit scope parameter has been included in the token request
                    // but was missing from the authorization request, the request MUST rejected.
                    // See http://tools.ietf.org/html/rfc6749#section-6
                    var scopes = ticket.Properties.GetScopes();
                    if (!scopes.Any()) {
                        Logger.LogError("token request cannot contain a scope");

                        await SendErrorPayloadAsync(new OpenIdConnectMessage {
                            Error = OpenIdConnectConstants.Errors.InvalidGrant,
                            ErrorDescription = "Token request cannot contain a scope parameter" +
                                               "if the authorization request didn't contain one"
                        });

                        return;
                    }

                    // When an explicit scope parameter has been included in the token request,
                    // the authorization server MUST ensure that it doesn't contain scopes
                    // that were not allowed during the authorization request.
                    else if (!scopes.ContainsSet(request.GetScopes())) {
                        Logger.LogError("authorization code does not contain matching scope");

                        await SendErrorPayloadAsync(new OpenIdConnectMessage {
                            Error = OpenIdConnectConstants.Errors.InvalidGrant,
                            ErrorDescription = "Token request doesn't contain a valid scope parameter"
                        });

                        return;
                    }
                }

                // Expose the authentication ticket extracted from the authorization
                // code or the refresh token before invoking ValidateTokenRequest.
                validatingContext.AuthenticationTicket = ticket;

                await Options.Provider.ValidateTokenRequest(validatingContext);

                if (!validatingContext.IsValidated) {
                    // Note: use invalid_request as the default error if none has been explicitly provided.
                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = validatingContext.Error ?? OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = validatingContext.ErrorDescription,
                        ErrorUri = validatingContext.ErrorUri
                    });

                    return;
                }

                if (request.IsAuthorizationCodeGrantType()) {
                    // Note: the authentication ticket is copied to avoid modifying the properties of the authorization code.
                    var context = new GrantAuthorizationCodeContext(Context, Options, request, ticket.Copy());
                    await Options.Provider.GrantAuthorizationCode(context);

                    if (!context.IsValidated) {
                        // Note: use invalid_grant as the default error if none has been explicitly provided.
                        await SendErrorPayloadAsync(new OpenIdConnectMessage {
                            Error = context.Error ?? OpenIdConnectConstants.Errors.InvalidGrant,
                            ErrorDescription = context.ErrorDescription,
                            ErrorUri = context.ErrorUri
                        });

                        return;
                    }

                    ticket = context.AuthenticationTicket;
                }

                else {
                    // Note: the authentication ticket is copied to avoid modifying the properties of the refresh token.
                    var context = new GrantRefreshTokenContext(Context, Options, request, ticket.Copy());
                    await Options.Provider.GrantRefreshToken(context);

                    if (!context.IsValidated) {
                        // Note: use invalid_grant as the default error if none has been explicitly provided.
                        await SendErrorPayloadAsync(new OpenIdConnectMessage {
                            Error = context.Error ?? OpenIdConnectConstants.Errors.InvalidGrant,
                            ErrorDescription = context.ErrorDescription,
                            ErrorUri = context.ErrorUri
                        });

                        return;
                    }

                    ticket = context.AuthenticationTicket;
                }

                // By default, when using the authorization code or the refresh token grants, the authentication ticket
                // extracted from the code/token is used as-is. If the developer didn't provide his own ticket
                // or didn't set an explicit expiration date, the ticket properties are reset to avoid aligning the
                // expiration date of the generated tokens with the lifetime of the authorization code/refresh token.
                if (ticket.Properties.IssuedUtc == validatingContext.AuthenticationTicket.Properties.IssuedUtc) {
                    ticket.Properties.IssuedUtc = null;
                }

                if (ticket.Properties.ExpiresUtc == validatingContext.AuthenticationTicket.Properties.ExpiresUtc) {
                    ticket.Properties.ExpiresUtc = null;
                }
            }

            // See http://tools.ietf.org/html/rfc6749#section-4.3
            // and http://tools.ietf.org/html/rfc6749#section-4.3.2
            else if (request.IsPasswordGrantType()) {
                var context = new GrantResourceOwnerCredentialsContext(Context, Options, request);
                await Options.Provider.GrantResourceOwnerCredentials(context);

                if (!context.IsValidated) {
                    // Note: use invalid_grant as the default error if none has been explicitly provided.
                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = context.Error ?? OpenIdConnectConstants.Errors.InvalidGrant,
                        ErrorDescription = context.ErrorDescription,
                        ErrorUri = context.ErrorUri
                    });

                    return;
                }

                ticket = context.AuthenticationTicket;
            }

            // See http://tools.ietf.org/html/rfc6749#section-4.4
            // and http://tools.ietf.org/html/rfc6749#section-4.4.2
            else if (request.IsClientCredentialsGrantType()) {
                var context = new GrantClientCredentialsContext(Context, Options, request);
                await Options.Provider.GrantClientCredentials(context);

                if (!context.IsValidated) {
                    // Note: use unauthorized_client as the default error if none has been explicitly provided.
                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = context.Error ?? OpenIdConnectConstants.Errors.UnauthorizedClient,
                        ErrorDescription = context.ErrorDescription,
                        ErrorUri = context.ErrorUri
                    });

                    return;
                }

                ticket = context.AuthenticationTicket;
            }

            // See http://tools.ietf.org/html/rfc6749#section-8.3
            else {
                var context = new GrantCustomExtensionContext(Context, Options, request);
                await Options.Provider.GrantCustomExtension(context);

                if (!context.IsValidated) {
                    // Note: use unsupported_grant_type as the default error if none has been explicitly provided.
                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = context.Error ?? OpenIdConnectConstants.Errors.UnsupportedGrantType,
                        ErrorDescription = context.ErrorDescription,
                        ErrorUri = context.ErrorUri
                    });

                    return;
                }

                ticket = context.AuthenticationTicket;
            }

            var notification = new TokenEndpointContext(Context, Options, request, ticket);
            await Options.Provider.TokenEndpoint(notification);

            if (notification.HandledResponse) {
                return;
            }

            // Flow the changes made to the ticket.
            ticket = notification.Ticket;

            // Ensure an authentication ticket has been provided:
            // a null ticket MUST result in an internal server error.
            if (ticket == null) {
                Logger.LogError("authentication ticket missing");

                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.ServerError
                });

                return;
            }

            if (!string.IsNullOrEmpty(request.ClientId)) {
                // Keep the original client_id parameter for later comparison.
                ticket.Properties.Items[OpenIdConnectConstants.Extra.ClientId] = request.ClientId;
            }

            if (!string.IsNullOrEmpty(request.Resource)) {
                // Keep the original resource parameter for later comparison.
                ticket.Properties.Items[OpenIdConnectConstants.Extra.Resource] = request.Resource;
            }

            if (!string.IsNullOrEmpty(request.Scope)) {
                // Keep the original scope parameter for later comparison.
                ticket.Properties.Items[OpenIdConnectConstants.Extra.Scope] = request.Scope;
            }

            if (clientNotification.IsValidated) {
                // Store a boolean indicating the client has been fully authenticated.
                ticket.Properties.Items[OpenIdConnectConstants.Extra.ClientAuthenticated] = "true";
            }

            var response = new OpenIdConnectMessage();

            // Determine whether an identity token should be returned and invoke CreateIdentityTokenAsync if necessary.
            // Note: by default, an identity token is always returned when the openid scope has been requested,
            // but the client application can use the response_type parameter to only include specific types of tokens.
            // When this parameter is missing, a token is always generated.
            if (string.IsNullOrEmpty(request.ResponseType) || request.ContainsResponseType(OpenIdConnectConstants.ResponseTypes.IdToken)) {
                // When receiving a grant_type=authorization_code or grant_type=refresh_token request,
                // only issue an id_token if the openid scope had been requested during the authorization request.
                if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType() ?
                    validatingContext.AuthenticationTicket.ContainsScope(OpenIdConnectConstants.Scopes.OpenId) :
                    request.ContainsScope(OpenIdConnectConstants.Scopes.OpenId)) {
                    // Make sure to create a copy of the authentication properties
                    // to avoid modifying the properties set on the original ticket.
                    var properties = ticket.Properties.Copy();

                    // When sliding expiration is disabled, the identity token added to the response
                    // cannot live longer than the refresh token that was used in the token request.
                    if (request.IsRefreshTokenGrantType() && !Options.UseSlidingExpiration &&
                        validatingContext.AuthenticationTicket.Properties.ExpiresUtc.HasValue &&
                        validatingContext.AuthenticationTicket.Properties.ExpiresUtc.Value <
                            (Options.SystemClock.UtcNow + Options.IdentityTokenLifetime)) {
                        properties.ExpiresUtc = validatingContext.AuthenticationTicket.Properties.ExpiresUtc;
                    }

                    response.IdToken = await CreateIdentityTokenAsync(ticket.Principal, properties, request, response);

                    // Ensure that an identity token is issued to avoid returning an invalid response.
                    // See http://openid.net/specs/openid-connect-core-1_0.html#TokenResponse
                    // and http://openid.net/specs/openid-connect-core-1_0.html#RefreshTokenResponse
                    if (string.IsNullOrEmpty(response.IdToken)) {
                        Logger.LogError("CreateIdentityTokenAsync returned no identity token.");

                        await SendErrorPayloadAsync(new OpenIdConnectMessage {
                            Error = OpenIdConnectConstants.Errors.ServerError,
                            ErrorDescription = "no valid identity token was issued"
                        });

                        return;
                    }
                }
            }

            // Determine whether an access token should be returned and invoke CreateAccessTokenAsync if necessary.
            // Note: by default, an access token is always returned, but the client application can use the response_type
            // parameter to only include specific types of tokens. When this parameter is missing, a token is always generated.
            if (string.IsNullOrEmpty(request.ResponseType) || request.ContainsResponseType(OpenIdConnectConstants.ResponseTypes.Token)) {
                // Make sure to create a copy of the authentication properties
                // to avoid modifying the properties set on the original ticket.
                var properties = ticket.Properties.Copy();

                // When sliding expiration is disabled, the access token added to the response
                // cannot live longer than the refresh token that was used in the token request.
                if (request.IsRefreshTokenGrantType() && !Options.UseSlidingExpiration &&
                    validatingContext.AuthenticationTicket.Properties.ExpiresUtc.HasValue &&
                    validatingContext.AuthenticationTicket.Properties.ExpiresUtc.Value <
                        (Options.SystemClock.UtcNow + Options.AccessTokenLifetime)) {
                    properties.ExpiresUtc = validatingContext.AuthenticationTicket.Properties.ExpiresUtc;
                }

                response.TokenType = OpenIdConnectConstants.TokenTypes.Bearer;
                response.AccessToken = await CreateAccessTokenAsync(ticket.Principal, properties, request, response);

                // Ensure that an access token is issued to avoid returning an invalid response.
                // See http://openid.net/specs/oauth-v2-multiple-response-types-1_0.html#Combinations
                if (string.IsNullOrEmpty(response.AccessToken)) {
                    Logger.LogError("CreateAccessTokenAsync returned no access token.");

                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.ServerError,
                        ErrorDescription = "no valid access token was issued"
                    });

                    return;
                }

                // properties.ExpiresUtc is automatically set by CreateAccessTokenAsync but the end user
                // is free to set a null value directly in the CreateAccessToken event.
                if (properties.ExpiresUtc.HasValue && properties.ExpiresUtc > Options.SystemClock.UtcNow) {
                    var lifetime = properties.ExpiresUtc.Value - Options.SystemClock.UtcNow;
                    var expiration = (long) (lifetime.TotalSeconds + .5);

                    response.ExpiresIn = expiration.ToString(CultureInfo.InvariantCulture);
                }
            }

            // Determine whether a refresh token should be returned and invoke CreateRefreshTokenAsync if necessary.
            // Note: by default, a refresh token is always returned when the offline_access scope has been requested,
            // but the client application can use the response_type parameter to only include specific types of tokens.
            // When this parameter is missing, a token is always generated.
            if (string.IsNullOrEmpty(request.ResponseType) || request.ContainsResponseType("refresh_token")) {
                // When receiving a grant_type=authorization_code or grant_type=refresh_token request,
                // ensure the offline_access scope had been requested during the authorization request.
                // For other grant types, the offline_access scope must be present in the token request.
                if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType() ?
                    validatingContext.AuthenticationTicket.ContainsScope(OpenIdConnectConstants.Scopes.OfflineAccess) :
                    request.ContainsScope(OpenIdConnectConstants.Scopes.OfflineAccess)) {
                    // Make sure to create a copy of the authentication properties
                    // to avoid modifying the properties set on the original ticket.
                    var properties = ticket.Properties.Copy();

                    // When sliding expiration is disabled, the refresh token added to the response
                    // cannot live longer than the refresh token that was used in the token request.
                    if (request.IsRefreshTokenGrantType() && !Options.UseSlidingExpiration &&
                        validatingContext.AuthenticationTicket.Properties.ExpiresUtc.HasValue &&
                        validatingContext.AuthenticationTicket.Properties.ExpiresUtc.Value <
                            (Options.SystemClock.UtcNow + Options.RefreshTokenLifetime)) {
                        properties.ExpiresUtc = validatingContext.AuthenticationTicket.Properties.ExpiresUtc;
                    }

                    response.RefreshToken = await CreateRefreshTokenAsync(ticket.Principal, properties, request, response);
                }
            }

            var payload = new JObject();

            foreach (var parameter in response.Parameters) {
                payload.Add(parameter.Key, parameter.Value);
            }

            var responseNotification = new TokenEndpointResponseContext(Context, Options, ticket, request, payload);
            await Options.Provider.TokenEndpointResponse(responseNotification);

            if (responseNotification.HandledResponse) {
                return;
            }

            using (var buffer = new MemoryStream())
            using (var writer = new JsonTextWriter(new StreamWriter(buffer))) {
                payload.WriteTo(writer);
                writer.Flush();

                Response.ContentLength = buffer.Length;
                Response.ContentType = "application/json;charset=UTF-8";

                Response.Headers[HeaderNames.CacheControl] = "no-cache";
                Response.Headers[HeaderNames.Pragma] = "no-cache";
                Response.Headers[HeaderNames.Expires] = "-1";

                buffer.Seek(offset: 0, loc: SeekOrigin.Begin);
                await buffer.CopyToAsync(Response.Body, 4096, Context.RequestAborted);
            }
        }

        private async Task<bool> InvokeProfileEndpointAsync() {
            OpenIdConnectMessage request;

            if (!string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Request.Method, "POST", StringComparison.OrdinalIgnoreCase)) {
                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed userinfo request has been received: " +
                        "make sure to use either GET or POST."
                });

                return true;
            }

            if (string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase)) {
                request = new OpenIdConnectMessage(Request.Query.ToDictionary());
            }

            else {
                // See http://openid.net/specs/openid-connect-core-1_0.html#FormSerialization
                if (string.IsNullOrWhiteSpace(Request.ContentType)) {
                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed userinfo request has been received: " +
                            "the mandatory 'Content-Type' header was missing from the POST request."
                    });

                    return true;
                }

                // May have media/type; charset=utf-8, allow partial match.
                if (!Request.ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)) {
                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed userinfo request has been received: " +
                            "the 'Content-Type' header contained an unexcepted value. " +
                            "Make sure to use 'application/x-www-form-urlencoded'."
                    });

                    return true;
                }

                var form = await Request.ReadFormAsync(Context.RequestAborted);

                request = new OpenIdConnectMessage(form.ToDictionary());
            }

            string token;
            if (!string.IsNullOrEmpty(request.AccessToken)) {
                token = request.AccessToken;
            }

            else {
                string header = Request.Headers[HeaderNames.Authorization];
                if (string.IsNullOrEmpty(header)) {
                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed userinfo request has been received."
                    });

                    return true;
                }

                if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) {
                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed userinfo request has been received."
                    });

                    return true;
                }

                token = header.Substring("Bearer ".Length);
                if (string.IsNullOrEmpty(token)) {
                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed userinfo request has been received."
                    });

                    return true;
                }
            }

            var ticket = await ReceiveAccessTokenAsync(token, request);
            if (ticket == null) {
                Logger.LogError("invalid token");

                // Note: an invalid token should result in an unauthorized response
                // but returning a 401 status would invoke the previously registered
                // authentication middleware and potentially replace it by a 302 response.
                // To work around this limitation, a 400 error is returned instead.
                // See http://openid.net/specs/openid-connect-core-1_0.html#UserInfoError
                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidGrant,
                    ErrorDescription = "Invalid token."
                });

                return true;
            }

            if (!ticket.Properties.ExpiresUtc.HasValue ||
                 ticket.Properties.ExpiresUtc < Options.SystemClock.UtcNow) {
                Logger.LogError("expired token");

                // Note: an invalid token should result in an unauthorized response
                // but returning a 401 status would invoke the previously registered
                // authentication middleware and potentially replace it by a 302 response.
                // To work around this limitation, a 400 error is returned instead.
                // See http://openid.net/specs/openid-connect-core-1_0.html#UserInfoError
                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidGrant,
                    ErrorDescription = "Expired token."
                });

                return true;
            }

            // Insert the userinfo request in the ASP.NET context.
            Context.SetOpenIdConnectRequest(request);

            var notification = new ProfileEndpointContext(Context, Options, request, ticket);

            var claims = new Dictionary<string, string> {
                // 'sub' is a mandatory claim but is not necessarily present as-is: when missing,
                // the name identifier extracted from the authentication ticket is used instead.
                // See http://openid.net/specs/openid-connect-core-1_0.html#UserInfoResponse
                [JwtRegisteredClaimNames.Sub] = ticket.Principal.GetClaim(JwtRegisteredClaimNames.Sub) ??
                                                ticket.Principal.GetClaim(ClaimTypes.NameIdentifier)
            };

            // The following claims are all optional and should be excluded when
            // no corresponding value has been found in the authentication ticket.
            if (ticket.ContainsScope(OpenIdConnectConstants.Scopes.Profile)) {
                claims[JwtRegisteredClaimNames.FamilyName] = ticket.Principal.GetClaim(ClaimTypes.Surname);
                claims[JwtRegisteredClaimNames.GivenName] = ticket.Principal.GetClaim(ClaimTypes.GivenName);
                claims[JwtRegisteredClaimNames.Birthdate] = ticket.Principal.GetClaim(ClaimTypes.DateOfBirth);
            }

            if (ticket.ContainsScope(OpenIdConnectConstants.Scopes.Email)) {
                claims[JwtRegisteredClaimNames.Email] = ticket.Principal.GetClaim(ClaimTypes.Email);
            };
            
            foreach (var claim in claims) {
                // Ignore claims whose value is null.
                if (string.IsNullOrEmpty(claim.Value)) {
                    continue;
                }

                notification.Claims.Add(claim);
            }

            await Options.Provider.ProfileEndpoint(notification);

            if (notification.HandledResponse) {
                return true;
            }

            else if (notification.Skipped) {
                return false;
            }

            var payload = new JObject();

            foreach (var claim in notification.Claims) {
                payload.Add(claim.Key, claim.Value);
            }

            var context = new ProfileEndpointResponseContext(Context, Options, request, payload);
            await Options.Provider.ProfileEndpointResponse(context);

            if (context.HandledResponse) {
                return true;
            }

            using (var buffer = new MemoryStream())
            using (var writer = new JsonTextWriter(new StreamWriter(buffer))) {
                payload.WriteTo(writer);
                writer.Flush();

                Response.ContentLength = buffer.Length;
                Response.ContentType = "application/json;charset=UTF-8";

                Response.Headers[HeaderNames.CacheControl] = "no-cache";
                Response.Headers[HeaderNames.Pragma] = "no-cache";
                Response.Headers[HeaderNames.Expires] = "-1";

                buffer.Seek(offset: 0, loc: SeekOrigin.Begin);
                await buffer.CopyToAsync(Response.Body, 4096, Context.RequestAborted);
            }

            return true;
        }

        private async Task InvokeValidationEndpointAsync() {
            OpenIdConnectMessage request;
            
            if (!string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Request.Method, "POST", StringComparison.OrdinalIgnoreCase)) {
                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed validation request has been received: " +
                        "make sure to use either GET or POST."
                });

                return;
            }

            if (string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase)) {
                request = new OpenIdConnectMessage(Request.Query.ToDictionary()) {
                    RequestType = OpenIdConnectRequestType.AuthenticationRequest
                };
            }

            else {
                // See http://openid.net/specs/openid-connect-core-1_0.html#FormSerialization
                if (string.IsNullOrEmpty(Request.ContentType)) {
                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed validation request has been received: " +
                            "the mandatory 'Content-Type' header was missing from the POST request."
                    });

                    return;
                }

                // May have media/type; charset=utf-8, allow partial match.
                if (!Request.ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)) {
                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed validation request has been received: " +
                            "the 'Content-Type' header contained an unexcepted value. " +
                            "Make sure to use 'application/x-www-form-urlencoded'."
                    });

                    return;
                }

                var form = await Request.ReadFormAsync(Context.RequestAborted);

                request = new OpenIdConnectMessage(form.ToDictionary()) {
                    RequestType = OpenIdConnectRequestType.AuthenticationRequest
                };
            }

            AuthenticationTicket ticket;
            if (!string.IsNullOrEmpty(request.AccessToken)) {
                ticket = await ReceiveAccessTokenAsync(request.AccessToken, request);
            }

            else if (!string.IsNullOrEmpty(request.IdToken)) {
                ticket = await ReceiveIdentityTokenAsync(request.IdToken, request);
            }

            else if (!string.IsNullOrEmpty(request.RefreshToken)) {
                ticket = await ReceiveRefreshTokenAsync(request.RefreshToken, request);
            }

            else {
                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed validation request has been received: " +
                        "either an identity token, an access token or a refresh token must be provided."
                });

                return;
            }

            if (ticket == null) {
                Logger.LogError("invalid token");

                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidGrant,
                    ErrorDescription = "Invalid access token received"
                });

                return;
            }

            if (!ticket.Properties.ExpiresUtc.HasValue ||
                 ticket.Properties.ExpiresUtc < Options.SystemClock.UtcNow) {
                Logger.LogError("expired token");

                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidGrant,
                    ErrorDescription = "Expired access token received"
                });

                return;
            }

            // Client applications and resource servers are strongly encouraged
            // to provide an audience parameter to mitigate confused deputy attacks.
            // See http://en.wikipedia.org/wiki/Confused_deputy_problem.
            var audiences = ticket.Properties.GetAudiences();
            if (audiences.Any() && !audiences.ContainsSet(request.GetAudiences())) {
                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidGrant,
                    ErrorDescription = "Invalid access token received: " +
                        "the audience doesn't correspond to the registered value"
                });

                return;
            }

            var notification = new ValidationEndpointContext(Context, Options, request, ticket);

            // Add the claims extracted from the access token.
            foreach (var claim in ticket.Principal.Claims) {
                notification.Claims.Add(claim);
            }

            await Options.Provider.ValidationEndpoint(notification);

            // Flow the changes made to the authentication ticket.
            ticket = notification.AuthenticationTicket;

            if (notification.HandledResponse) {
                return;
            }

            var payload = new JObject();

            payload.Add("audiences", JArray.FromObject(ticket.Properties.GetAudiences()));
            payload.Add("expires_in", ticket.Properties.ExpiresUtc.Value);

            payload.Add("claims", JArray.FromObject(
                from claim in notification.Claims
                select new { type = claim.Type, value = claim.Value }
            ));

            var context = new ValidationEndpointResponseContext(Context, Options, payload);
            await Options.Provider.ValidationEndpointResponse(context);

            if (context.HandledResponse) {
                return;
            }

            using (var buffer = new MemoryStream())
            using (var writer = new JsonTextWriter(new StreamWriter(buffer))) {
                payload.WriteTo(writer);
                writer.Flush();

                Response.ContentLength = buffer.Length;
                Response.ContentType = "application/json;charset=UTF-8";

                Response.Headers[HeaderNames.CacheControl] = "no-cache";
                Response.Headers[HeaderNames.Pragma] = "no-cache";
                Response.Headers[HeaderNames.Expires] = "-1";

                buffer.Seek(offset: 0, loc: SeekOrigin.Begin);
                await buffer.CopyToAsync(Response.Body, 4096, Context.RequestAborted);
            }
        }

        private async Task<bool> InvokeLogoutEndpointAsync() {
            OpenIdConnectMessage request = null;

            // In principle, logout requests must be made via GET. Nevertheless,
            // POST requests are also allowed so that the inner application can display a logout form.
            // See https://openid.net/specs/openid-connect-session-1_0.html#RPLogout
            if (!string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Request.Method, "POST", StringComparison.OrdinalIgnoreCase)) {
                return await SendErrorPageAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed logout request has been received: " +
                        "make sure to use either GET or POST."
                });
            }

            if (string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase)) {
                request = new OpenIdConnectMessage(Request.Query.ToDictionary()) {
                    RequestType = OpenIdConnectRequestType.LogoutRequest
                };
            }

            else {
                // See http://openid.net/specs/openid-connect-core-1_0.html#FormSerialization
                if (string.IsNullOrEmpty(Request.ContentType)) {
                    return await SendErrorPageAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed logout request has been received: " +
                            "the mandatory 'Content-Type' header was missing from the POST request."
                    });
                }

                // May have media/type; charset=utf-8, allow partial match.
                if (!Request.ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)) {
                    return await SendErrorPageAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed logout request has been received: " +
                            "the 'Content-Type' header contained an unexcepted value. " +
                            "Make sure to use 'application/x-www-form-urlencoded'."
                    });
                }

                var form = await Request.ReadFormAsync(Context.RequestAborted);

                request = new OpenIdConnectMessage(form.ToDictionary()) {
                    RequestType = OpenIdConnectRequestType.LogoutRequest
                };
            }

            // Store the logout request in the ASP.NET context.
            Context.SetOpenIdConnectRequest(request);

            // Note: post_logout_redirect_uri is not a mandatory parameter.
            // See http://openid.net/specs/openid-connect-session-1_0.html#RPLogout
            if (!string.IsNullOrEmpty(request.PostLogoutRedirectUri)) {
                var clientNotification = new ValidateClientLogoutRedirectUriContext(Context, Options, request);
                await Options.Provider.ValidateClientLogoutRedirectUri(clientNotification);

                if (clientNotification.IsRejected) {
                    Logger.LogVerbose("Unable to validate client information");

                    return await SendErrorPageAsync(new OpenIdConnectMessage {
                        Error = clientNotification.Error,
                        ErrorDescription = clientNotification.ErrorDescription,
                        ErrorUri = clientNotification.ErrorUri
                    });
                }
            }

            var notification = new LogoutEndpointContext(Context, Options, request);
            await Options.Provider.LogoutEndpoint(notification);

            if (notification.HandledResponse) {
                return true;
            }

            return false;
        }
    }
}