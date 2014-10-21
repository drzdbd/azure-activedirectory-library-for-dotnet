﻿//----------------------------------------------------------------------
// Copyright (c) Microsoft Open Technologies, Inc.
// All Rights Reserved
// Apache License 2.0
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
// http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Microsoft.IdentityModel.Clients.ActiveDirectory
{
    internal class AcquireTokenInteractiveHandler : AcquireTokenHandlerBase
    {
        internal AuthorizationResult authorizationResult;

        private Uri redirectUri;

        private string redirectUriRequestParameter;

        private IAuthorizationParameters authorizationParameters;

        private readonly string extraQueryParameters;

        private readonly IWebUI webUi;

        private readonly UserIdentifier userId;

        public AcquireTokenInteractiveHandler(Authenticator authenticator, TokenCache tokenCache, string resource, string clientId, Uri redirectUri, IAuthorizationParameters parameters, UserIdentifier userId, string extraQueryParameters, IWebUI webUI, bool callSync)
            : base(authenticator, tokenCache, resource, new ClientKey(clientId), TokenSubjectType.User, callSync)
        {
            this.redirectUri = PlatformPlugin.PlatformInformation.ValidateRedirectUri(redirectUri, this.CallState);

            if (!string.IsNullOrWhiteSpace(this.redirectUri.Fragment))
            {
                var ex = new ArgumentException(AdalErrorMessage.RedirectUriContainsFragment, "redirectUri");
                PlatformPlugin.Logger.LogException(this.CallState, ex);
                throw ex;
            }

            this.authorizationParameters = parameters;

            this.redirectUriRequestParameter = PlatformPlugin.PlatformInformation.GetRedirectUriAsString(this.redirectUri, this.CallState);

            if (userId == null)
            {
                var ex = new ArgumentNullException("userId", AdalErrorMessage.SpecifyAnyUser);
                PlatformPlugin.Logger.LogException(this.CallState, ex);
                throw ex;
            }

            this.userId = userId;

            if (!string.IsNullOrEmpty(extraQueryParameters) && extraQueryParameters[0] == '&')
            {
                extraQueryParameters = extraQueryParameters.Substring(1);
            }

            this.extraQueryParameters = extraQueryParameters;

            this.webUi = webUI;

            this.UniqueId = userId.UniqueId;
            this.DisplayableId = userId.DisplayableId;
            this.UserIdentifierType = userId.Type;

            this.LoadFromCache = (tokenCache != null && parameters != null && PlatformPlugin.PlatformInformation.GetCacheLoadPolicy(parameters));

            this.SupportADFS = true;
        }

        protected override async Task PreTokenRequest()
        {
            await base.PreTokenRequest();

            // We do not have async interactive API in .NET, so we call this synchronous method instead.
            await this.AcquireAuthorizationAsync();
            this.VerifyAuthorizationResult();
        }

        internal async Task AcquireAuthorizationAsync()
        {
            Uri authorizationUri = this.CreateAuthorizationUri(await IncludeFormsAuthParamsAsync());
            string resultUri = await this.webUi.AcquireAuthorizationAsync(authorizationUri, this.redirectUri, this.CallState);
            this.authorizationResult = OAuth2Response.ParseAuthorizeResponse(resultUri, this.CallState);
        }

        internal async Task<bool> IncludeFormsAuthParamsAsync()
        {
            return (await PlatformPlugin.PlatformInformation.IsUserLocalAsync(this.CallState)) && PlatformPlugin.PlatformInformation.IsDomainJoined();
        }

        internal async Task<Uri> CreateAuthorizationUriAsync(Guid correlationId)
        {
            this.CallState.CorrelationId = correlationId;
            await this.Authenticator.UpdateFromTemplateAsync(this.CallState);
            return this.CreateAuthorizationUri(false);
        }
        protected override void AddAditionalRequestParameters(RequestParameters requestParameters)
        {
            requestParameters[OAuthParameter.GrantType] = OAuthGrantType.AuthorizationCode;
            requestParameters[OAuthParameter.Code] = this.authorizationResult.Code;
            requestParameters[OAuthParameter.RedirectUri] = this.redirectUriRequestParameter;
        }

        protected override void PostTokenRequest(AuthenticationResult result)
        {
            base.PostTokenRequest(result);
            if ((this.DisplayableId == null && this.UniqueId == null) || this.UserIdentifierType == UserIdentifierType.OptionalDisplayableId)
            {
                return;
            }

            string uniqueId = (result.UserInfo != null && result.UserInfo.UniqueId != null) ? result.UserInfo.UniqueId : "NULL";
            string displayableId = (result.UserInfo != null) ? result.UserInfo.DisplayableId : "NULL";

            if (this.UserIdentifierType == UserIdentifierType.UniqueId && string.Compare(uniqueId, this.UniqueId, StringComparison.Ordinal) != 0)
            {
                var ex = new AdalUserMismatchException(this.UniqueId, uniqueId);
                PlatformPlugin.Logger.LogException(this.CallState, ex);
                throw ex;
            }

            if (this.UserIdentifierType == UserIdentifierType.RequiredDisplayableId && string.Compare(displayableId, this.DisplayableId, StringComparison.OrdinalIgnoreCase) != 0)
            {
                var ex = new AdalUserMismatchException(this.DisplayableId, displayableId);
                PlatformPlugin.Logger.LogException(this.CallState, ex);
                throw ex;
            }
        }

        private Uri CreateAuthorizationUri(bool includeFormsAuthParam)
        {
            string loginHint = null;

            if (!userId.IsAnyUser
                && (userId.Type == UserIdentifierType.OptionalDisplayableId
                    || userId.Type == UserIdentifierType.RequiredDisplayableId))
            {
                loginHint = userId.Id;
            }

            RequestParameters requestParameters = this.CreateAuthorizationRequest(loginHint, includeFormsAuthParam);

            var authorizationUri = new Uri(new Uri(this.Authenticator.AuthorizationUri), "?" + requestParameters);
            authorizationUri = new Uri(HttpHelper.CheckForExtraQueryParameter(authorizationUri.AbsoluteUri));

            return authorizationUri;
        }

        private RequestParameters CreateAuthorizationRequest(string loginHint, bool includeFormsAuthParam)
        {
            RequestParameters authorizationRequestParameters = new RequestParameters(this.Resource, this.ClientKey);
            authorizationRequestParameters[OAuthParameter.ResponseType] = OAuthResponseType.Code;

            authorizationRequestParameters[OAuthParameter.RedirectUri] = this.redirectUriRequestParameter;

            if (!string.IsNullOrWhiteSpace(loginHint))
            {
                authorizationRequestParameters[OAuthParameter.LoginHint] = loginHint;
            }

            if (this.CallState != null && this.CallState.CorrelationId != Guid.Empty)
            {
                authorizationRequestParameters[OAuthParameter.CorrelationId] = this.CallState.CorrelationId.ToString();
            }

            if (this.authorizationParameters != null)
            {
                PlatformPlugin.PlatformInformation.AddPromptBehaviorQueryParameter(this.authorizationParameters, authorizationRequestParameters);
            }

            if (includeFormsAuthParam)
            {
                authorizationRequestParameters[OAuthParameter.FormsAuth] = OAuthValue.FormsAuth;
            }

            AdalIdHelper.AddAsQueryParameters(authorizationRequestParameters);

            if (!string.IsNullOrWhiteSpace(extraQueryParameters))
            {
                // Checks for extraQueryParameters duplicating standard parameters
                Dictionary<string, string> kvps = EncodingHelper.ParseKeyValueList(extraQueryParameters, '&', false, this.CallState);
                foreach (KeyValuePair<string, string> kvp in kvps)
                {
                    if (authorizationRequestParameters.ContainsKey(kvp.Key))
                    {
                        var ex = new AdalException(AdalError.DuplicateQueryParameter, string.Format(AdalErrorMessage.DuplicateQueryParameterTemplate, kvp.Key));
                        PlatformPlugin.Logger.LogException(this.CallState, ex);
                        throw ex;
                    }
                }

                authorizationRequestParameters.ExtraQueryParameter = extraQueryParameters;
            }

            return authorizationRequestParameters;
        }

        private void VerifyAuthorizationResult()
        {
            if (this.authorizationResult.Error == OAuthError.LoginRequired)
            {
                var ex = new AdalException(AdalError.UserInteractionRequired);
                PlatformPlugin.Logger.LogException(this.CallState, ex);
                throw ex;
            }

            if (this.authorizationResult.Status != AuthorizationStatus.Success)
            {
                var ex = new AdalServiceException(this.authorizationResult.Error, this.authorizationResult.ErrorDescription);
                PlatformPlugin.Logger.LogException(this.CallState, ex);
                throw ex;
            }
        }
    }
}