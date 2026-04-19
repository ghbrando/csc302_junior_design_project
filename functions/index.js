const { onDocumentCreated } = require("firebase-functions/v2/firestore");
const admin = require("firebase-admin");
admin.initializeApp();

exports.autoCompletePayouts = onDocumentCreated("payouts/{payoutId}", async (event) => {
    const snap = event.data;
    if (!snap) return null;

    const data = snap.data();
    if (data.status !== "Pending") return null;

    const payoutId = event.params.payoutId;

    await new Promise(resolve => setTimeout(resolve, 5 * 60 * 1000));

    const current = await snap.ref.get();
    if (current.data().status !== "Pending") return null;

    await snap.ref.update({ status: "Completed" });
    console.log(`Payout ${payoutId} marked as Completed.`);
    return null;
});