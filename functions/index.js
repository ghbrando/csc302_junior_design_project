const { onDocumentCreated } = require("firebase-functions/v2/firestore");
const admin = require("firebase-admin");
admin.initializeApp();

// Current Timer: 5 minutes
// If changed, run following terminal command after:
// firebase deploy --only functions
exports.autoCompletePayouts = onDocumentCreated(
    {
        document: "payouts/{payoutId}",
        // minutes * 60 + 60 forr buffer
        timeoutSeconds: 360
    },
    async (event) => {
        const snap = event.data;
        if (!snap) return null;

        const data = snap.data();
        if (data.status !== "Pending") return null;

        const payoutId = event.params.payoutId;

        // minutes * 60 * 1000
        await new Promise(resolve => setTimeout(resolve, 5 * 60 * 1000));

        const current = await snap.ref.get();
        if (current.data().status !== "Pending") return null;

        await snap.ref.update({ status: "Completed" });
        console.log(`Payout ${payoutId} marked as Completed.`);
        return null;
    }
);