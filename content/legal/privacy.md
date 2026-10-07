<!-- starter draft: delete this line once a lawyer has reviewed this page. -->
<!--
  Notes for the site's owner. Nothing between these marks is ever sent to a browser.

  A starting point, not legal advice: see the notes at the top of terms.md. It describes what the code does today
  (see docs/architecture.md: "Accounts", "Media", "Feedback and insights" and "Clean-up"). If you change how the site
  keeps or shares data, change this page too.

  {{ServiceProviders}} becomes a list of the outside services this site actually uses, read from its settings:
  the AI providers chosen under Admin → AI, Stripe when payments are on, and the hosting, email, storage and
  monitoring services named in the Legal settings.
-->

# Privacy Policy

_Last updated: October 7, 2026_

This policy explains what {{Operator}} ("we", "us") collects when you use {{SiteName}} at {{SiteUrl}}, why, who else sees it, how long we keep it, and the choices you have.

The short version: we collect what the games need and little else. We don't sell personal information, we don't show ads, and we don't use advertising or analytics cookies.

## What we collect

**If you host (you have an account)**

- your name, your email address, and your password, which we store only in a scrambled form (a "hash") that can't be turned back into the password;
- your parties, the mysteries and escape rooms you write, edit or have the AI write, the files you upload, and your saved party settings;
- your plan: your free trial, and, if you pay, the dates of your plan and the references Stripe gives us for you and your subscription. We never see or store your card number;
- when you created your account, and what the AI features cost for your games.

**If you're a guest (no account)**

- the name you type to join a party. A first name or a nickname is all the game needs;
- what you do in the game: the character you play, your accusations and puzzle answers, and any questions you ask the characters;
- your private notes, if you write any;
- a costume selfie, only if you choose to add one (see below);
- a rating of the game and, on Adults games only, a short comment. These are stored without your name.

**If you watch a party** (when the host allows it): the name you type to watch.

**On every visit**: like every website, our server sees your IP address and your browser's details with each request, and keeps short-lived logs of them to keep the site working and secure. Hosts have a cookie that keeps them signed in; a guest's phone keeps its place at the party in the browser's own storage. Both are needed for the site to work.

## Costume selfies

- **Optional.** A guest can add a photo of their costume; nobody has to.
- **Cleaned before anyone sees it.** Our server makes a fresh, smaller copy of the photo and throws the original away. The copy carries none of the hidden information phones add to photos, such as where it was taken.
- **Seen only at that party:** on the host's TV, on the phones at the party, and on the party's recap page if the host shares its link.
- **Yours to remove.** Remove it from your phone at any time, and it's deleted from our server straight away.
- **Deleted automatically** {{FinishedPartyDays}} days after the party, or {{IdlePartyDays}} days after it was last used if it never finished.
- **Never sent to an AI provider,** and never used for anything else.

## How we use it

- to run the games and your account;
- to send account emails: confirming your address, resetting your password, and changes to your account. No marketing;
- to take payments and give you the games you paid for;
- to keep the site and its users safe, for example by limiting how fast anyone can try party codes or passwords;
- to fix problems and improve the games, using ratings and play statistics that don't name anyone;
- when the law requires it.

## Who else sees it

**The people at your party.** Everyone at a party sees the names at that party, on the TV and on the phones. The host sees the guests' names and how the game went. Notes stay on their writer's phone. A recap page shows the names and selfies of that party to anyone the host gives its link to, and the host can stop sharing it at any time.

**The services that help us run the site.** Each gets only what its job needs, and may use it only to do that job for us:

{{ServiceProviders}}

**When the law requires it**, or to protect someone's safety or rights.

**If the site changes hands**, the new owner would take on this policy.

We don't sell personal information, and we don't share it for advertising.

## How long we keep it

- **Games that never finished** are deleted, with everything their guests shared, {{IdlePartyDays}} days after they were last used.
- **Finished games** lose their guests' notes, selfies and places at the party {{FinishedPartyDays}} days after the party. The game's outcome and the names guests typed stay, so the host's recap keeps working, until the host deletes their account (or asks us to delete it sooner).
- **Leaderboards** keep times and the number of players. The players' names are shown only to the host who ran the game.
- **Ratings and play statistics** are kept without names.
- **Host accounts** are kept until you delete yours. Deleting your account removes your account, your plan, your parties with their guests' information, and your own mysteries, rooms and files. We keep leaderboard times and the record of AI costs without your name. Stripe keeps its own records of your payments, as tax law requires.
- **Messages from Stripe** about payments are kept for 90 days.
- **Backups** of our database are kept for up to {{BackupMonths}} months, so something deleted from the site disappears from the backups as they're replaced.

## AI features

When the site's AI features are on, the AI providers listed above receive the text they need to play their part: the story, the characters, the names guests typed and the questions they ask the characters, to write answers, hints and the game master's lines, and the story's words, to make voices and pictures. They never receive email addresses, payment details, private notes or photos.

<!-- REVIEW: check each AI provider's terms for whether it keeps what it receives, or trains on it, and say so here. Most providers' paid API terms say they don't train on it. -->

## Children

- **Hosts must be 18 or older.** We don't knowingly let anyone younger create an account.
- **Family games are made so that children can play as guests,** at a party run by an adult who invited them. Guests don't create accounts and never give us an email address.
- **For children under 13, the adult in charge decides what's shared.** We suggest a nickname rather than a real name, and a costume selfie only with a parent's permission. Family games never ask guests to type comments.
- **Everything a guest shares is deleted automatically,** as described above. A parent can ask us to delete it sooner: email {{ContactEmail}} with the party's code or the host's name.
- If you think a child under 13 gave us personal information without a parent's permission, contact us and we'll delete it.

<!-- REVIEW: COPPA. The site isn't aimed at children for sign-up, but Family games are meant for them as guests, and a photo of a child is "personal information" under COPPA. The design keeps it small (no accounts, nicknames, optional photos deleted automatically), but ask whether you need the host to confirm a parent's consent before a child's selfie, and whether this section is enough. -->

## Your choices and rights

- **Hosts** can see and change their details, download a copy of their information (Your account → Download my data), and delete their account, all from Your account. You can also start any party without the AI features.
- **Guests** can remove their selfie at any time, and can ask the host, or us, to delete what they shared at a party.
- **Depending on the state you live in** (for example California, Colorado, Connecticut, Virginia or Texas), you may have the right to know what we hold about you, to get a copy, to correct it, to have it deleted, and to opt out of its sale or its use for targeted advertising (we do neither). Email {{ContactEmail}} to use any of these rights. We'll answer within 45 days, may need to confirm it's you, and won't treat you differently for asking.

## Security

Connections to the site are encrypted (HTTPS). Passwords and the codes that let guests back into a party are stored only as hashes, and the keys the site uses to reach AI providers are encrypted. Each host can reach only their own parties, mysteries, rooms and files, and we test that. No system is perfectly secure: if something happens to your information, we'll tell you as the law requires.

## The United States only

{{SiteName}} is for people in the United States. Your information is stored and processed where we and our service providers run our computers, which may be outside your state.

## Changes to this policy

If we change this policy, we'll change the date at the top. Before a change that matters takes effect, we'll tell hosts by email or on the site.

## Contact

{{Operator}}, {{ContactEmail}}
