using System;
using System.Text;
using System.Threading.Tasks;
using AppleAuth;
using AppleAuth.Enums;
using AppleAuth.Extensions;
using AppleAuth.Interfaces;
using Firebase.Auth;
using Firebase.Extensions;
using Firebase.Firestore;
using UnityEngine;

namespace InGameMoney
{
    public class AppleAuth : IAccountBase
    {
        private readonly FirebaseAuth auth;
        private readonly UserData userData;
        private readonly IAppleAuthManager appleAuthManager;

        public AppleAuth(IAppleAuthManager appleAuthManager)
        {
            auth = FirebaseAuth.DefaultInstance;
            userData = ((UserDataAccess)AccountTest.UserDataAccess).UserData;
            this.appleAuthManager = appleAuthManager;
            ObjectManager.Instance.FirstBootLogs.text = $"New AppleAuth";
        }
        public void Validate()
        {
            if (!AccountTest.Instance.SignedIn && (string.IsNullOrEmpty(userData.AccountData.mailAddress) || string.IsNullOrEmpty(userData.AccountData.password)))
            {
                AccountTest.Instance.SignOutBecauseLocalDataIsEmpty();
                return;
            }
            Print.GreenLog($">>>> AppleAuth Email {auth.CurrentUser.Email}");
            InitializeLoginMenu();
        }

        private void InitializeLoginMenu()
        {
            Print.GreenLog($">>>>> InitializeLoginMenu");
            if (appleAuthManager == null)
            {
                Print.GreenLog($">>>>> Initialize appleAuthManager is null, Unsupported platform");
                return;
            }
            
            // If at any point we receive a credentials revoked notification, we delete the stored User ID, and go back to login
            appleAuthManager.SetCredentialsRevokedCallback(result =>
            {
                Print.GreenLog($">>>>>Received revoked callback {result}");
                AccountTest.Instance.SignOut();
                PlayerPrefs.DeleteKey(AccountTest.AppleUserIdKey);
            });
            
            // If we have an Apple User Id available, get the credential status for it
            if (PlayerPrefs.HasKey(AccountTest.AppleUserIdKey))
            {
                Print.GreenLog($">>>>> We have an Apple User Id available, get the credential status for it");
                var storedAppleUserId = PlayerPrefs.GetString(AccountTest.AppleUserIdKey);
                CheckCredentialStatusForUserId(storedAppleUserId);
            }
            // If we do not have an stored Apple User Id, attempt a quick login
            else
            {
                Print.GreenLog($">>>>> we do not have an stored Apple User Id, attempt a quick login");
                PerformQuickLoginWithFirebase();
            }
        }

        private void CheckCredentialStatusForUserId(string appleUserId)
        {
            // If there is an apple ID available, we should check the credential state
            appleAuthManager.GetCredentialState(
                appleUserId,
                state =>
                {
                    switch (state)
                    {
                        // If it's authorized, login with that user id
                        case CredentialState.Authorized:
                            Print.GreenLog($">>>>> CheckCredentialStatusForUserId Authorized {appleUserId}");
                            LoginByAppleId();
                            return;
                        
                        // If it was revoked, or not found, we need a new sign in with apple attempt
                        // Discard previous apple user id
                        case CredentialState.Revoked:
                        case CredentialState.NotFound:
                            Print.GreenLog($">>>>> CheckCredentialStatusForUserId CredentialState Revoked or NotFound  {appleUserId}");
                            AccountTest.Instance.SignOut();
                            PlayerPrefs.DeleteKey(AccountTest.AppleUserIdKey);
                            return;
                        case CredentialState.Transferred:
                            ObjectManager.Instance.FirstBootLogs.text = "CredentialState.Transferred";
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(state), state, null);
                    }
                },
                error =>
                {
                    var authorizationErrorCode = error.GetAuthorizationErrorCode();
                    ObjectManager.Instance.FirstBootLogs.text = $"Error while trying to get credential state {authorizationErrorCode} {error}";
                    AccountTest.Instance.SignOut();
                });
        }
        
        public void PerformLoginWithAppleIdAndFirebase()
        {
            ObjectManager.Instance.FirstBootLogs.text = $"PerformLoginWithAppleIdAndFirebase";
            var rawNonce = AuthUtility.GenerateRandomString(32);
            var nonce = AuthUtility.GenerateSHA256NonceFromRawNonce(rawNonce);

            var loginArgs = new AppleAuthLoginArgs(
                LoginOptions.IncludeEmail | LoginOptions.IncludeFullName,
                nonce);
			
            appleAuthManager.LoginWithAppleId(
                loginArgs,
                credential =>
                {
                    if (credential is IAppleIDCredential appleIdCredential)
                    {
                        var userId = appleIdCredential.User;
                        PlayerPrefs.SetString(AccountTest.AppleUserIdKey, userId);
                        PerformFirebaseAppleAuthentication(appleIdCredential, rawNonce, false);
                    }
                },
                error =>
                {
                    ObjectManager.Instance.FirstBootLogs.text = $"PerformLoginWithAppleIdAndFirebase error {error}";
                });
        }
        
        private void PerformQuickLoginWithFirebase()
        {
            var rawNonce = AuthUtility.GenerateRandomString(32);
            var nonce = AuthUtility.GenerateSHA256NonceFromRawNonce(rawNonce);
            var quickLoginArgs = new AppleAuthQuickLoginArgs(nonce);
			
            appleAuthManager.QuickLogin(
                quickLoginArgs,
                credential =>
                {
                    if (credential is IAppleIDCredential appleIDCredential)
                    {
                        PerformFirebaseAppleAuthentication(appleIDCredential, rawNonce, true);
                    }
                },
                error =>
                {
                    ObjectManager.Instance.FirstBootLogs.text = $"Perform QuickLogin WithFirebase error {error}";
                });
        }
        
        private void PerformFirebaseAppleAuthentication(
            IAppleIDCredential appleIdCredential,
            string rawNonce, 
            bool fromQuickLogin = false)
        {
            Print.GreenLog($">>>> PerformFirebaseAppleAuthentication fromQuickLogin {fromQuickLogin}");
            ObjectManager.Instance.FirstBootLogs.text = $"PerformFirebaseAuthentication found token {appleIdCredential.IdentityToken}";
            var firebaseAppleCredential = GetFirebaseAppleCredential(appleIdCredential, rawNonce);

            if (auth?.CurrentUser != null && auth.CurrentUser.IsAnonymous)
            {
                LinkGuestAccountWithApple(firebaseAppleCredential);
            }
            else
            {
                SignInWithCredential(firebaseAppleCredential);
            }
        }

        private async void SignInWithCredential(Credential firebaseAppleCredential)
        {
            var signInTask = auth.SignInWithCredentialAsync(firebaseAppleCredential)
                .ContinueWithOnMainThread(task => task);

            await signInTask;
			
            if (signInTask.Result.IsCanceled)
            {
                Print.GreenLog(">>>> Firebase auth was canceled");
            }
            else if (AccountTest.IsFaultedTask(signInTask.Result, true))
            {
                Print.GreenLog($">>>> Firebase auth failed {signInTask.Result.Exception}");
            }
            else
            {
                var newUser = signInTask.Result.Result;
                PlayerPrefs.SetString(AccountTest.FirebaseSignedWithAppleKey, "Yes");

                var data = new User
                {
                    Email = newUser.Email,
                    MoneyBalance = 0,
                    Password = $"vw-apple-pass@{newUser.UserId}",
                    SignUpTimeStamp = FieldValue.ServerTimestamp
                };

                Print.GreenLog($">>>> Firebase SignInWithCredentialAsync apple succeed Email {data.Email} signedIn {AccountTest.Instance.SignedIn} " + $"UserId {newUser.UserId} " + $"FirebaseSignedWithAppleKey {PlayerPrefs.GetString(AccountTest.FirebaseSignedWithAppleKey)}");
                
                await ObjectManager.Instance.FirestoreRegistrationAsync(data);
                AccountTest.Instance.WriteUserData(data);
                LoginByAppleId();
            }
        }

        private void LinkGuestAccountWithApple(Credential firebaseAppleCredential)
        {
            var currentUser = auth.CurrentUser;
            currentUser.LinkWithCredentialAsync(firebaseAppleCredential)
                .ContinueWith(task =>
                {
                    if (task.IsCanceled) {
                        Print.GreenLog( $">>>> LinkWithCredentialAsync was canceled.");
                        return;
                    }
                    if (task.IsFaulted) {
                        Print.GreenLog( $">>>> LinkWithCredentialAsync encountered an error: {task.Exception}");
                        return;
                    }

                    var newUser = task.Result;
                    Print.GreenLog($">>>> OpenGameView from Apple Credentials successfully linked to Firebase userId {newUser.UserId}");
                    AccountTest.LinkAccountToFirestore(newUser.Email, $"vw-apple-pass@{newUser.UserId}");
                    AccountTest.Instance.SetAuthButtonInteraction();
                    AccountTest.Instance.OpenGameView();
                }, TaskScheduler.FromCurrentSynchronizationContext());
            
        }

        private static Credential GetFirebaseAppleCredential(IAppleIDCredential appleIdCredential, string rawNonce)
        {
            var identityToken = Encoding.UTF8.GetString(appleIdCredential.IdentityToken);
            var authorizationCode = Encoding.UTF8.GetString(appleIdCredential.AuthorizationCode);
            var firebaseAppleCredential = OAuthProvider.GetCredential(
                "apple.com",
                identityToken,
                rawNonce,
                authorizationCode);
            Print.GreenLog($">>>>>>> GetFirebaseAppleCredential firebaseAppleCredential.IsValid() {firebaseAppleCredential.IsValid()}");
            return firebaseAppleCredential;
        }
        
        private static void LoginByAppleId()
        {
            Print.GreenLog($">>>> OpenGameView from LoginByAppleId");
            AccountTest.Instance.Login();
            AccountTest.Instance.UpdatePurchaseAndShop();
            ObjectManager.Instance.ResetInputField();
            AccountTest.Instance.OpenGameView();
            AccountTest.Instance.RegisterGuestAccount.interactable = false;
        }
    }
}