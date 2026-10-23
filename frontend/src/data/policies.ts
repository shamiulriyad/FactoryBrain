export type PolicyDoc = {
  id: string;
  title: string;
  titleBn: string;
  type: "return" | "supplier-agreement" | "shipping" | "privacy";
  body: string;
  bodyBn: string;
  effectiveDate: string;
};

export const policies: PolicyDoc[] = [
  {
    id: "policy:return-1",
    title: "Return Policy (BD)",
    titleBn: "পণ্য ফেরত নীতি (বাংলাদেশ)",
    type: "return",
    effectiveDate: "2025-11-01",
    body: `Customers may return unworn, unused items within 7 days of delivery for a full refund.
Returned items must include original packaging. Perishable grocery items are not returnable
unless damaged in transit. Refunds are issued to the original payment method within 5 business days
of receiving the returned item. Return shipping is paid by the customer except in cases of our error.

For Bangladesh: cash-on-delivery orders are refunded via bKash/Nagad/Rocket within 3 business days
after the returned item is received at our Dhaka warehouse.`,
    bodyBn: `গ্রাহকরা ডেলিভারির ৭ দিনের মধ্যে অব্যবহৃত পণ্য ফেরত দিয়ে সম্পূর্ণ রিফান্ড পেতে পারেন।
ফেরত পণ্যসামগ্রীর সাথে মূল প্যাকেজিং থাকতে হবে। নষ্ট হওয়া মুদি পণ্য ফেরত যোগ্য নয়,
যদি না পরিবহনে ক্ষতিগ্রস্ত হয়। রিফান্ড ৫ কার্যদিবসের মধ্যে মূল পেমেন্ট পদ্ধতিতে দেওয়া হয়।

বাংলাদেশের জন্য: ক্যাশ অন ডেলিভারি অর্ডারের রিফান্ড ঢাকা গুদামে পণ্য পৌঁছানোর
৩ কার্যদিবসের মধ্যে বিকাশ/নগদ/রকেটে দেওয়া হয়।`
  },
  {
    id: "policy:supplier-agreement-1",
    title: "Supplier Agreement — Narayanganj Textiles & Sylhet Tea & Beauty",
    titleBn: "সরবরাহকারী চুক্তি — নারায়ণগঞ্জ টেক্সটাইল ও সিলেট চা",
    type: "supplier-agreement",
    effectiveDate: "2025-08-15",
    body: `Standard supplier agreement: 30-day payment terms, 2% early-payment discount within 7 days.
Quality acceptance window: 5 days from delivery. Rejection requires photographic evidence within the window.
Force majeure clauses align with Bangladesh labour and weather calendars. Late delivery penalties:
1% of order value per day, capped at 10%.

Termination: 30 days written notice from either party. Outstanding invoices remain payable.`,
    bodyBn: `স্ট্যান্ডার্ড সরবরাহকারী চুক্তি: ৩০ দিনের পেমেন্ট শর্ত, ৭ দিনের মধ্যে ২% আগাম পেমেন্ট ছাড়।
মান গ্রহণযোগ্যতা: ডেলিভারির ৫ দিনের মধ্যে। প্রত্যাখ্যানের জন্য উইন্ডোর মধ্যে ফটোগ্রাফিক প্রমাণ প্রয়োজন।
দেরি ডেলিভারির জরিমানা: প্রতিদিন অর্ডার মূল্যের ১%, সর্বোচ্চ ১০%।`
  }
];