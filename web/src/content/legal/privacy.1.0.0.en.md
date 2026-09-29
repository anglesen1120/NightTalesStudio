# Privacy Policy

NightTalesStudio turns an approved story into a narrated video episode and, when you choose, publishes it to a channel you have connected. This policy explains what personal data the service processes, why, for how long, and what you can do about it. It describes the personal, owner-operated phase of the service.

## 1. Who we are and how to contact us

NightTalesStudio ("we", "us", "the service") is a personal, owner-operated service. The operator is the controller of the processing described in this policy.

For any privacy question, request or complaint, including a request to exercise one of the rights in section 10, contact the operator at the privacy contact address. The operator's legal name and the privacy contact address are **placeholders**: the operator has not yet published them, and must set them before this policy is shown to anyone other than the operator. Until they are set, no person other than the operator may use the service.

If you are not satisfied with our answer, you can also raise the matter with the competent data-protection authority for your country.

## 2. Data we collect

- Account data: your e-mail address, your display name, your password stored only as a hash, session and device records, and multi-factor authentication secrets when you enable that protection.
- Content you create: your story ideas and prompts, the story versions and segments you approve, the source links and notes you add, your episode settings, and the audio, video and image files the service renders from them.
- Channel connection data: for each connected platform account (YouTube or TikTok), the platform account identifier and display name, the OAuth access and refresh tokens, the granted scopes and the connection status, the platform payloads needed to resume or reconcile an upload such as an upload session identifier, and, for published items, the external post identifier and its address.
- Usage and billing data: the operations you run, the metered model and processing usage behind them with its cost, your credit balance and credit lots, and the payments the payment provider reports with their status. In the personal phase there is no registration, no credit purchase and no payment, so these rows appear only once the service is opened to users other than the operator.
- Logs and technical data: request logs with the timestamp, route, status code, error code and trace identifier, and the audit rows the service writes for actions such as recording an acceptance of this policy or opening a report case.

We never ask you for a platform password, and we never store one (section 7). Access tokens, upload session identifiers and similar secrets do not appear in our logs.

## 3. Data from YouTube API Services

NightTalesStudio uses YouTube API Services. This section describes the data we receive through them and what we do with it.

- What we access: the identifier and title of the channel you connect, and the processing status of the videos the service uploaded for you.
- What we store: the channel identifier and title, the video identifiers with their processing status, and the OAuth tokens that let the service act on your behalf. The platform terms call this stored connection data the "Authorized Data".
- What we share: we never sell Authorized Data, and we never share it with third parties for advertising. Only the sub-processors in section 8 handle it, and only for the step that needs it.
- How we use it: to perform the actions you ask for, such as connecting a channel, uploading, scheduling, and reading back the processing status, and to keep the stored data fresh as section 5 requires.

Your use of the YouTube features is also subject to the [YouTube Terms of Service](https://www.youtube.com/t/terms), and Google's own handling of data in its services is described in the [Google Privacy Policy](https://policies.google.com/privacy).

## 4. How you revoke access

- In the app: use the disconnect action on the channel screen. The service asks Google to revoke the grant, deletes the stored tokens immediately, and starts the deletion described in section 5.
- From Google: open the [Google security settings page](https://security.google.com/settings/security/permissions) and remove the service's access to your account. The service notices that the grant is gone at its next token check and starts the same deletion.

Revoking access stops future uploads. It does not delete a video that is already published on YouTube; delete that video on YouTube itself.

## 5. Retention and deletion

These are the windows that apply to the data described above.

- When you revoke access in the app, the stored Authorized Data is deleted within 7 days.
- When you revoke access from the Google security settings page, the stored Authorized Data is deleted within 30 days.
- The stored Authorized Data, meaning the channel title and the video identifiers with their status, is refreshed from the platform or deleted at least every 30 days.
- A request to delete your account is completed within 7 days.
- Backups are kept for 30 days: deleted data leaves our database and storage backups within that window, a restore re-applies every deletion that completed after the backup was taken before the service reopens, and nothing is kept beyond that window.
- A few rows survive a deletion by design. When you delete an episode, its metered usage rows stay for cost accounting. When a channel connection is removed, the publication records stay but without the external identifiers. When an account is deleted, the credit ledger and payment rows stay for as long as accounting law requires, and so do the security and staff-action audit rows and your acceptance records, which are the proof of what you accepted; in the account case the user reference is replaced by a pseudonym.

## 6. Upload consent

Every upload has its own consent record. Before a publication is uploaded you see the consent text that applies to it, the version of that text is stored with the publication, and the upload never starts without the record.

For a series that runs in autopilot, consent is derived from the versioned authorization you gave for that series. The episode still gets its own record, and that record is created from the series authorization only after a veto window: during the veto window you are notified, and you can edit the episode or cancel the publication. If you cancel, no consent is recorded and nothing is uploaded.

The text you accept for an upload is the versioned publication consent text pc.v1, which lives in the service's repository as one file per language: Templates/Consent/pc.v1.vi.md and Templates/Consent/pc.v1.en.md. This policy cites that text and does not repeat it.

## 7. We never ask for your YouTube password

We never ask you for your YouTube, Google or TikTok password, and you should never type one into the service. Connecting a channel happens on the platform's own authorization screen: you sign in on the platform, and the service receives a token that you can revoke at any time, never your credentials. Anyone who asks you for a platform password in the name of this service is not acting for us.

## 8. Sub-processors

The service uses the processors listed below. Each one receives only what its step needs, and none of them receives a platform password.

- OpenAI: generating the story text, the narration script and the images from the inputs you provide.
- A hosting provider: running the application, the database and the background jobs.
- A storage provider: storing the rendered audio, video and images and the source files you upload.
- A payment provider: processing card payments once billing is enabled; the payment details go to that provider directly, and the service never stores a card number.
- An e-mail provider: delivering service e-mails such as address verification, security alerts and publication notices.

Adding or replacing a processor is a change to this policy and is handled as section 10 describes.

## 9. Staff access, logging and security alerts

The service is owner-operated, so in the personal phase the operator is the only administrator.

- Account, billing and session data: an administrator can read it when handling a support request or an abuse report.
- Story text, sources and video: they are opened only inside a moderation or a report case.
- Logging: every such view is written to the audit trail, with the identifier of the administrator who performed it.
- No impersonation: the service has no sign-in-as-another-user feature, and every administrative action is attributed to the administrator who performed it.
- Security alerts: alerts about staff and account actions, such as role changes, multi-factor resets and settings changes, go only to internal owners and on-call, and are never shared with third parties.

## 10. International transfers, security, your rights, children and changes

- International transfers: some processing happens outside Vietnam. Our sub-processors, OpenAI included, process data on servers in other countries, and those transfers rely on the providers' contractual safeguards.
- Security measures: traffic is encrypted in transit, platform tokens and upload session identifiers are encrypted at rest, passwords are stored only as hashes, administrative access is limited and logged, and data that is no longer needed expires as section 5 describes.
- Your rights: you can ask to see the data the service holds about you, to correct it, to delete it, and to receive a machine-readable copy of it. Account deletion follows section 5. Send requests to the contact address in section 1.
- Children: the service is not intended for anyone under 18, and we do not knowingly process the data of a person under 18. If you believe a minor has used the service, contact us and we will delete that account.
- Changes to this policy: a change is published as a new version with a new effective date, both shown at the top of this page. Before a material change takes effect the service asks you to accept the new version, and it blocks connecting a channel and starting an autopilot series until you have accepted it.
