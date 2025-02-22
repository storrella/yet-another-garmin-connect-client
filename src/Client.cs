﻿using Flurl.Http;
using NLog;
using OAuth;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using YetAnotherGarminConnectClient.Dto;
using YetAnotherGarminConnectClient.Dto.Garmin;
using YetAnotherGarminConnectClient.Dto.Garmin.Fit;

namespace YetAnotherGarminConnectClient
{
    internal partial class Client : IClient
    {
        private readonly string _consumerKey;
        private readonly string _consumerSecret;
        private AuthStatus _authStatus;
        private string _mfaCsrfToken = string.Empty;
        CookieJar _cookieJar = null;

        private static readonly object _commonQueryParams = new
        {
            id = "gauth-widget",
            embedWidget = "true",
            gauthHost = URLs.SSO_EMBED_URL,
            redirectAfterAccountCreationUrl = URLs.SSO_EMBED_URL,
            redirectAfterAccountLoginUrl = URLs.SSO_EMBED_URL,
            service = URLs.SSO_EMBED_URL,
            source = URLs.SSO_EMBED_URL,
        };

        private ILogger _logger => NLog.LogManager.GetLogger("Client");
        public OAuth2Token OAuth2Token { get; private set; }
        public DateTime _oAuth2TokenValidUntil { get; private set; }


        private Client() { }
        internal Client(string consumerKey, string consumerSecret)
        {

            _consumerKey = consumerKey;
            _consumerSecret = consumerSecret;

        }

        public bool IsOAuthValid
        {
            get
            {
                if (this.OAuth2Token == null)
                {
                    return false;
                }

                return DateTime.UtcNow < _oAuth2TokenValidUntil;
            }
        }

        public async Task<WeightUploadResult> UploadWeight(GarminWeightScaleDTO weightScaleDTO, UserProfileSettings userProfileSettings, string? mfaCode = "")
        {
            var result = new WeightUploadResult();

            try
            {
                if (!IsOAuthValid)
                {
                    if (string.IsNullOrEmpty(mfaCode))
                    {
                        var authResult = await this.Authenticate(weightScaleDTO.Email, weightScaleDTO.Password);
                        if (!authResult.IsSuccess)
                        {
                            result.MFACodeRequested = authResult.MFACodeRequested;
                            result.AuthStatus = _authStatus;
                            result.Logs = Logger.GetLogs();
                            result.ErrorLogs = Logger.GetErrorLogs();
                            return result;
                        }
                    }
                    else
                    {
                        var authResult = await this.CompleteMFAAuthAsync(mfaCode);
                        if (!authResult.IsSuccess)
                        {
                            result.MFACodeRequested = authResult.MFACodeRequested;
                            result.AuthStatus = _authStatus;
                            result.Logs = Logger.GetLogs();
                            result.ErrorLogs = Logger.GetErrorLogs();
                            return result;
                        }
                    }
                }
            }
            catch (GarminClientException ex)
            {
                result.AuthStatus = _authStatus;
                _logger.Error(ex, ex.Message);
            }
            catch (Exception ex)
            {
                result.AuthStatus = _authStatus;
                _logger.Error(ex, ex.Message);
            }

            if (IsOAuthValid)
            {
                try
                {
                    string fitFilePath = string.Empty;
                    try
                    {
                        fitFilePath = FitFileCreator.CreateWeightBodyCompositionFitFile(weightScaleDTO, userProfileSettings);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Problem with creating fit file");
                    }

                    if (!string.IsNullOrEmpty(fitFilePath))
                    {
                        var response = await UploadActivity(fitFilePath, ".fit");
                        if (response != null && response.DetailedImportResult != null)
                        {
                            result.UploadId = response.DetailedImportResult.uploadId;
                            result.IsSuccess = true;
                        }

                    }

                }
                catch (GarminClientException ex)
                {
                    result.AuthStatus = _authStatus;
                    _logger.Error(ex, ex.Message);
                }
                catch (Exception ex)
                {
                    result.AuthStatus = _authStatus;
                    _logger.Error(ex, ex.Message);
                }
            }

            result.AuthStatus = _authStatus;
            result.Logs = Logger.GetLogs();
            result.ErrorLogs = Logger.GetErrorLogs();

            return result;
        }

        public async Task<UploadResponse> UploadActivity(string filePath, string format)
        {
            var fileName = Path.GetFileName(filePath);
            UploadResponse response = null;

            try
            {
                response = await $"{URLs.UPLOAD_URL}/{format}"
                 .WithOAuthBearerToken(OAuth2Token.Access_Token)
                 .WithHeader("NK", "NT")
                 .WithHeader("origin", URLs.ORIGIN)
                 .WithHeader("User-Agent", MagicStrings.USER_AGENT)
                 .AllowHttpStatus("2xx,409")
                 .PostMultipartAsync((data) =>
                 {
                     data.AddFile("\"file\"", path: filePath, contentType: "application/octet-stream", fileName: $"\"{fileName}\"");
                 })
                 .ReceiveJson<UploadResponse>();
            }


            catch (FlurlHttpException ex)
            {
                this._logger.Error(ex, "Failed to upload activity to Garmin. Flur Exception.");
            }
            catch (Exception ex)
            {
                this._logger.Error(ex, "Failed to upload activity to Garmin.");
            }
            finally
            {
                if (response != null)
                {
                    var result = response.DetailedImportResult;

                    if (result.failures.Any())
                    {
                        foreach (var failure in result.failures)
                        {
                            if (failure.Messages.Any())
                            {
                                foreach (var message in failure.Messages)
                                {
                                    if (message.Code == 202)
                                    {
                                        _logger.Info("Activity already uploaded", result.fileName);
                                    }
                                    else
                                    {
                                        _logger.Error("Failed to upload activity to Garmin. Message:", message);
                                    }
                                }
                            }
                        }
                    }
                }

            }
            return response;
        }

    }
}
