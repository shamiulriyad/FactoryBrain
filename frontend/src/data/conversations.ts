export type Conversation = {
  id: string;
  customerId: string;
  channel: "whatsapp" | "facebook" | "instagram";
  daysAgo: number;
  messages: Array<{
    role: "customer" | "agent";
    text: string;
    textBn: string;
  }>;
  theme: "delivery" | "return" | "product" | "price" | "stock";
};

// Realistic mixed Bangla/English threads.
export const conversations: Conversation[] = [
  {
    id: "conv-1",
    customerId: "c-00128",
    channel: "whatsapp",
    daysAgo: 2,
    messages: [
      { role: "customer", text: "Did my order arrive?", textBn: "আমার অর্ডার কি এসেছে?" },
      { role: "agent", text: "Yes, delivered yesterday. Was the parcel in good condition?", textBn: "হ্যাঁ, গতকাল ডেলিভারি হয়েছে। পার্সেল ভালো অবস্থায় ছিল তো?" },
      { role: "customer", text: "Yes, but the colour looks different from the website. Can I return?", textBn: "হ্যাঁ, তবে রঙ ওয়েবসাইটের থেকে আলাদা। ফেরত দিতে পারব?" },
      { role: "agent", text: "Of course. Please share a photo and we will arrange pickup within 48 hours.", textBn: "অবশ্যই। একটি ছবি শেয়ার করুন আমরা ৪৮ ঘন্টার মধ্যে পিকআপের ব্যবস্থা করব।" }
    ],
    theme: "return"
  },
  {
    id: "conv-2",
    customerId: "c-00312",
    channel: "facebook",
    daysAgo: 5,
    messages: [
      { role: "customer", text: "When will Heritage Apparel Pro be back in stock?", textBn: "হেরিটেজ অ্যাপারেল প্রো কবে আবার স্টকে আসবে?" },
      { role: "agent", text: "Restock expected within 10 days. I can notify you the moment it arrives.", textBn: "১০ দিনের মধ্যে রিস্টক আসবে। আসার সাথে সাথে আপনাকে জানাতে পারি।" },
      { role: "customer", text: "Please do. Also do you have a similar colour in Coral Apparel Lite?", textBn: "দয়া করে। আর করাল অ্যাপারেল লাইটে কি একই রঙ আছে?" }
    ],
    theme: "stock"
  },
  {
    id: "conv-3",
    customerId: "c-00044",
    channel: "whatsapp",
    daysAgo: 8,
    messages: [
      { role: "customer", text: "Delivery was 3 days late, disappointed.", textBn: "ডেলিভারি ৩ দিন দেরি হয়েছে, হতাশ।" },
      { role: "agent", text: "I am sorry. I have flagged this with the courier and credited your account ৳200.", textBn: "আমি দুঃখিত। আমি কুরিয়ারকে জানিয়েছি এবং আপনার অ্যাকাউন্টে ২০০ টাকা জমা দিয়েছি।" },
      { role: "customer", text: "Thanks.", textBn: "ধন্যবাদ।" }
    ],
    theme: "delivery"
  },
  {
    id: "conv-4",
    customerId: "c-00099",
    channel: "instagram",
    daysAgo: 12,
    messages: [
      { role: "customer", text: "Do you ship to Chattogram?", textBn: "আপনারা কি চট্টগ্রামে শিপিং করেন?" },
      { role: "agent", text: "Yes, 2–3 days. Shipping is ৳80 flat.", textBn: "হ্যাঁ, ২–৩ দিন। শিপিং ৮০ টাকা সমতল।" }
    ],
    theme: "delivery"
  },
  {
    id: "conv-5",
    customerId: "c-00077",
    channel: "whatsapp",
    daysAgo: 3,
    messages: [
      { role: "customer", text: "I keep buying the same grocery items every week. Any loyalty discount?", textBn: "আমি প্রতি সপ্তাহে একই মুদি কিনি। কোনো লয়্যালটি ছাড় আছে?" },
      { role: "agent", text: "We can enrol you in our weekly-basket plan. 8% off recurring orders, free shipping over ৳1,500.", textBn: "আমরা আপনাকে সাপ্তাহিক বাস্কেট প্ল্যানে যুক্ত করতে পারি। পুনরাবৃত্ত অর্ডারে ৮% ছাড়, ১,৫০০ টাকার উপরে ফ্রি শিপিং।" },
      { role: "customer", text: "Sign me up.", textBn: "আমাকে যুক্ত করুন।" }
    ],
    theme: "price"
  }
];