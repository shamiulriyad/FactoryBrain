import { useBusinessStore } from "@/store/business.store";
import type { BusinessProfile } from "@/store/business.store";

export const businessService = {
  getProfile(): BusinessProfile {
    return useBusinessStore.getState().profile;
  },
  updateProfile(patch: Partial<BusinessProfile>) {
    useBusinessStore.getState().setProfile(patch);
  },
  isOnboarded(): boolean {
    return useBusinessStore.getState().onboardingComplete;
  },
  completeOnboarding() {
    useBusinessStore.getState().completeOnboarding();
  },
  name(): string {
    const profile = useBusinessStore.getState().profile;
    return profile.industry || "Your business";
  }
};